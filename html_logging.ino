#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <SPI.h>
#include <Preferences.h>

// ============================================================
// WIFI
// ============================================================

const char* WIFI_SSID = "Mastar Bari";
const char* WIFI_PASSWORD = "13477471";

WebServer server(80);
Preferences preferences;

// ============================================================
// PINOUT
// ============================================================

// TX1 ... TX8
const uint8_t TX_PINS[8] = {
  13, 12, 14, 27, 26, 25, 33, 32
};

// MCP3008
constexpr uint8_t MCP_CS   = 5;
constexpr uint8_t MCP_SCK  = 18;
constexpr uint8_t MCP_MISO = 19;
constexpr uint8_t MCP_MOSI = 23;

constexpr float ADC_VREF = 3.300f;
constexpr uint16_t ADC_MAX = 1023;

constexpr uint32_t MCP_SPI_HZ = 2000000;

SPISettings mcpSPI(
  MCP_SPI_HZ,
  MSBFIRST,
  SPI_MODE0
);

// ============================================================
// USER-TUNABLE SETTINGS
// ============================================================

struct Settings {
  uint16_t thresholdMargin = 20;
  uint8_t consecutive = 2;

  uint8_t burstCycles = 8;

  uint32_t blankUs = 300;
  uint32_t windowUs = 12000;
  uint32_t noiseUs = 2500;

  uint16_t settleMs = 30;
  uint16_t scanIntervalMs = 800;

  // cm per microsecond
  float soundCmPerUs = 0.0343f;

  // subtract this from measured first-crossing TOF
  int32_t tofOffsetUs = 0;

  // manually measured target distance
  float knownDistanceCm = 30.0f;

  // true = CH0+ / CH1-
  // false = single-ended CH0
  bool pseudoDifferential = true;
};

Settings cfg;

// ============================================================
// RESULTS
// ============================================================

struct NoiseStats {
  uint16_t minimum;
  uint16_t maximum;
  float average;
  uint16_t threshold;
  uint32_t samples;
};

struct EchoResult {
  bool detected;

  uint16_t peakADC;
  float peakVoltage;

  uint32_t firstCrossUs;
  uint32_t peakTimeUs;

  float rawDistanceCm;
  float calibratedDistanceCm;

  uint32_t samples;
};

NoiseStats lastNoise[8];
EchoResult lastEcho[8];

uint32_t scanId = 0;

bool continuousMode = false;
bool scanning = false;

uint32_t lastContinuousScanMs = 0;

// ============================================================
// SETTINGS STORAGE
// ============================================================

void loadSettings() {
  preferences.begin("ultra-cal", false);

  cfg.thresholdMargin =
    preferences.getUShort("thr", 20);

  cfg.consecutive =
    preferences.getUChar("cons", 2);

  cfg.burstCycles =
    preferences.getUChar("burst", 8);

  cfg.blankUs =
    preferences.getULong("blank", 300);

  cfg.windowUs =
    preferences.getULong("window", 12000);

  cfg.noiseUs =
    preferences.getULong("noise", 2500);

  cfg.settleMs =
    preferences.getUShort("settle", 30);

  cfg.scanIntervalMs =
    preferences.getUShort("interval", 800);

  cfg.soundCmPerUs =
    preferences.getFloat("sound", 0.0343f);

  cfg.tofOffsetUs =
    preferences.getInt("offset", 0);

  cfg.knownDistanceCm =
    preferences.getFloat("known", 30.0f);

  cfg.pseudoDifferential =
    preferences.getBool("diff", true);
}

void saveSettings() {
  preferences.putUShort(
    "thr",
    cfg.thresholdMargin
  );

  preferences.putUChar(
    "cons",
    cfg.consecutive
  );

  preferences.putUChar(
    "burst",
    cfg.burstCycles
  );

  preferences.putULong(
    "blank",
    cfg.blankUs
  );

  preferences.putULong(
    "window",
    cfg.windowUs
  );

  preferences.putULong(
    "noise",
    cfg.noiseUs
  );

  preferences.putUShort(
    "settle",
    cfg.settleMs
  );

  preferences.putUShort(
    "interval",
    cfg.scanIntervalMs
  );

  preferences.putFloat(
    "sound",
    cfg.soundCmPerUs
  );

  preferences.putInt(
    "offset",
    cfg.tofOffsetUs
  );

  preferences.putFloat(
    "known",
    cfg.knownDistanceCm
  );

  preferences.putBool(
    "diff",
    cfg.pseudoDifferential
  );
}

// ============================================================
// TX CONTROL
// ============================================================

void allTxOff() {
  for (uint8_t i = 0; i < 8; i++) {
    digitalWrite(TX_PINS[i], LOW);
  }
}

inline void waitUntilUs(uint32_t targetUs) {
  while ((int32_t)(micros() - targetUs) < 0) {
    // intentional precision wait
  }
}

// ============================================================
// 40 kHz BURST
//
// Each period = 25 us.
// Alternate 12/13 and 13/12 us.
// ============================================================

uint32_t sendBurst(uint8_t txIndex) {
  allTxOff();

  uint8_t pin = TX_PINS[txIndex];

  uint32_t startUs = micros();
  uint32_t nextUs = startUs;

  for (uint8_t cycle = 0;
       cycle < cfg.burstCycles;
       cycle++) {

    uint8_t highUs =
      (cycle & 1) ? 13 : 12;

    uint8_t lowUs =
      25 - highUs;

    digitalWrite(pin, HIGH);

    nextUs += highUs;
    waitUntilUs(nextUs);

    digitalWrite(pin, LOW);

    nextUs += lowUs;
    waitUntilUs(nextUs);
  }

  digitalWrite(pin, LOW);

  return startUs;
}

// ============================================================
// MCP3008
// ============================================================

inline uint16_t mcpTransfer(
  uint8_t commandByte
) {
  digitalWrite(MCP_CS, LOW);

  SPI.transfer(0x01);

  uint8_t highByte =
    SPI.transfer(commandByte);

  uint8_t lowByte =
    SPI.transfer(0x00);

  digitalWrite(MCP_CS, HIGH);

  return
    ((uint16_t)(highByte & 0x03) << 8)
    |
    lowByte;
}

// Single-ended CH0
inline uint16_t readSingleCH0() {
  return mcpTransfer(0x80);
}

// Differential:
// CH0 = +
// CH1 = -
inline uint16_t readDiffCH0CH1() {
  return mcpTransfer(0x00);
}

inline uint16_t readEnvelopeADC() {
  if (cfg.pseudoDifferential) {
    return readDiffCH0CH1();
  }

  return readSingleCH0();
}

float adcToVoltage(uint16_t adc) {
  return
    ((float)adc * ADC_VREF)
    /
    1023.0f;
}

// ============================================================
// NOISE
// ============================================================

NoiseStats measureNoise() {
  NoiseStats n;

  n.minimum = ADC_MAX;
  n.maximum = 0;
  n.average = 0.0f;
  n.threshold = 0;
  n.samples = 0;

  uint32_t sum = 0;

  uint32_t startUs = micros();

  SPI.beginTransaction(mcpSPI);

  while (
    (uint32_t)(micros() - startUs)
      <
    cfg.noiseUs
  ) {
    uint16_t adc =
      readEnvelopeADC();

    if (adc < n.minimum) {
      n.minimum = adc;
    }

    if (adc > n.maximum) {
      n.maximum = adc;
    }

    sum += adc;

    n.samples++;
  }

  SPI.endTransaction();

  if (n.samples > 0) {
    n.average =
      (float)sum /
      (float)n.samples;
  }

  uint32_t thresholdValue =
    (uint32_t)n.maximum
    +
    cfg.thresholdMargin;

  if (thresholdValue > ADC_MAX) {
    thresholdValue = ADC_MAX;
  }

  n.threshold =
    (uint16_t)thresholdValue;

  return n;
}

// ============================================================
// ECHO CAPTURE
// ============================================================

EchoResult captureEcho(
  uint32_t txStartUs,
  uint16_t threshold
) {
  EchoResult result = {};

  result.detected = false;

  result.peakADC = 0;
  result.peakVoltage = 0.0f;

  result.firstCrossUs = 0;
  result.peakTimeUs = 0;

  result.rawDistanceCm = NAN;
  result.calibratedDistanceCm = NAN;

  result.samples = 0;

  uint32_t burstDurationUs =
    (uint32_t)cfg.burstCycles * 25UL;

  uint32_t receiveStartUs =
    burstDurationUs
    +
    cfg.blankUs;

  // Wait until burst + blanking has finished.
  while (
    (uint32_t)(micros() - txStartUs)
      <
    receiveStartUs
  ) {
  }

  uint32_t windowStartUs =
    micros();

  uint8_t consecutiveCount = 0;
  uint32_t crossingCandidateUs = 0;

  SPI.beginTransaction(mcpSPI);

  while (
    (uint32_t)(micros() - windowStartUs)
      <
    cfg.windowUs
  ) {
    uint32_t sampleTimeUs =
      micros();

    uint16_t adc =
      readEnvelopeADC();

    result.samples++;

    // Peak amplitude
    if (adc > result.peakADC) {
      result.peakADC = adc;

      result.peakTimeUs =
        sampleTimeUs
        -
        txStartUs;
    }

    // First validated threshold crossing
    if (!result.detected) {

      if (adc >= threshold) {

        if (consecutiveCount == 0) {
          crossingCandidateUs =
            sampleTimeUs
            -
            txStartUs;
        }

        consecutiveCount++;

        if (
          consecutiveCount
            >=
          cfg.consecutive
        ) {
          result.detected = true;

          result.firstCrossUs =
            crossingCandidateUs;
        }

      } else {
        consecutiveCount = 0;
      }
    }
  }

  SPI.endTransaction();

  result.peakVoltage =
    adcToVoltage(
      result.peakADC
    );

  if (result.detected) {

    // RAW distance: no calibration correction
    result.rawDistanceCm =
      (
        (float)result.firstCrossUs
        *
        cfg.soundCmPerUs
      )
      /
      2.0f;

    // Corrected TOF
    int32_t correctedUs =
      (int32_t)result.firstCrossUs
      -
      cfg.tofOffsetUs;

    if (correctedUs < 0) {
      correctedUs = 0;
    }

    result.calibratedDistanceCm =
      (
        (float)correctedUs
        *
        cfg.soundCmPerUs
      )
      /
      2.0f;
  }

  return result;
}

// ============================================================
// TEST ONE TRANSMITTER
// ============================================================

void testChannel(uint8_t tx) {
  allTxOff();

  delay(cfg.settleMs);

  lastNoise[tx] =
    measureNoise();

  uint32_t txStartUs =
    sendBurst(tx);

  lastEcho[tx] =
    captureEcho(
      txStartUs,
      lastNoise[tx].threshold
    );
}

// ============================================================
// SERIAL CSV OUTPUT
// ============================================================

void printScanCSV() {
  Serial.println(
    "ScanID,Known_cm,TX,Mode,NoiseAvg,"
    "NoiseMax,Threshold,PeakADC,PeakV,"
    "FirstCross_us,PeakTime_us,"
    "RawDistance_cm,CalDistance_cm"
  );

  for (uint8_t i = 0; i < 8; i++) {

    Serial.print(scanId);
    Serial.print(",");

    Serial.print(
      cfg.knownDistanceCm,
      2
    );
    Serial.print(",");

    Serial.print(i + 1);
    Serial.print(",");

    Serial.print(
      cfg.pseudoDifferential
        ?
      "DIFF"
        :
      "SINGLE"
    );
    Serial.print(",");

    Serial.print(
      lastNoise[i].average,
      2
    );
    Serial.print(",");

    Serial.print(
      lastNoise[i].maximum
    );
    Serial.print(",");

    Serial.print(
      lastNoise[i].threshold
    );
    Serial.print(",");

    Serial.print(
      lastEcho[i].peakADC
    );
    Serial.print(",");

    Serial.print(
      lastEcho[i].peakVoltage,
      4
    );
    Serial.print(",");

    if (lastEcho[i].detected) {

      Serial.print(
        lastEcho[i].firstCrossUs
      );
      Serial.print(",");

      Serial.print(
        lastEcho[i].peakTimeUs
      );
      Serial.print(",");

      Serial.print(
        lastEcho[i].rawDistanceCm,
        3
      );
      Serial.print(",");

      Serial.println(
        lastEcho[i].calibratedDistanceCm,
        3
      );

    } else {

      Serial.print("NA,");
      Serial.print(
        lastEcho[i].peakTimeUs
      );
      Serial.println(",NA,NA");
    }
  }
}

// ============================================================
// FULL SCAN
// ============================================================

void scanAll() {
  if (scanning) {
    return;
  }

  scanning = true;

  for (uint8_t tx = 0; tx < 8; tx++) {
    testChannel(tx);
  }

  scanId++;

  printScanCSV();

  scanning = false;
}

// ============================================================
// MEDIAN HELPERS
// ============================================================

float medianFloat(
  float* values,
  uint8_t count
) {
  if (count == 0) {
    return NAN;
  }

  // Simple sort, tiny array only.
  for (uint8_t i = 0; i < count; i++) {
    for (uint8_t j = i + 1; j < count; j++) {
      if (values[j] < values[i]) {
        float temp = values[i];
        values[i] = values[j];
        values[j] = temp;
      }
    }
  }

  if (count & 1) {
    return values[count / 2];
  }

  return
    (
      values[count / 2 - 1]
      +
      values[count / 2]
    )
    /
    2.0f;
}

float getMedianDistance() {
  float values[8];
  uint8_t count = 0;

  for (uint8_t i = 0; i < 8; i++) {
    if (lastEcho[i].detected) {
      values[count++] =
        lastEcho[i].calibratedDistanceCm;
    }
  }

  return medianFloat(
    values,
    count
  );
}

float getMedianRawDistance() {
  float values[8];
  uint8_t count = 0;

  for (uint8_t i = 0; i < 8; i++) {
    if (lastEcho[i].detected) {
      values[count++] =
        lastEcho[i].rawDistanceCm;
    }
  }

  return medianFloat(
    values,
    count
  );
}

float getMedianTOF() {
  float values[8];
  uint8_t count = 0;

  for (uint8_t i = 0; i < 8; i++) {
    if (lastEcho[i].detected) {
      values[count++] =
        (float)lastEcho[i].firstCrossUs;
    }
  }

  return medianFloat(
    values,
    count
  );
}

// ============================================================
// JSON HELPERS
// ============================================================

String jsonFloat(
  float value,
  unsigned int decimals = 3
) {
  if (isnan(value)) {
    return "null";
  }

  return String(value, decimals);
}

// ============================================================
// JSON DATA
// ============================================================

String createDataJSON() {
  String json;

  json.reserve(5000);

  float medianDistance =
    getMedianDistance();

  float medianRaw =
    getMedianRawDistance();

  float medianTOF =
    getMedianTOF();

  float errorCm = NAN;
  float offsetEstimateUs = NAN;

  if (!isnan(medianDistance)) {
    errorCm =
      medianDistance
      -
      cfg.knownDistanceCm;
  }

  if (!isnan(medianTOF)) {

    float expectedTOF =
      (
        2.0f
        *
        cfg.knownDistanceCm
      )
      /
      cfg.soundCmPerUs;

    offsetEstimateUs =
      medianTOF
      -
      expectedTOF;
  }

  json += "{";

  json += "\"scanId\":";
  json += String(scanId);

  json += ",\"scanning\":";
  json += scanning ? "true" : "false";

  json += ",\"continuous\":";
  json += continuousMode ? "true" : "false";

  json += ",\"mode\":\"";
  json += cfg.pseudoDifferential
    ? "PSEUDO-DIFF"
    : "SINGLE";
  json += "\"";

  // Settings
  json += ",\"settings\":{";

  json += "\"known\":";
  json += String(
    cfg.knownDistanceCm,
    2
  );

  json += ",\"threshold\":";
  json += String(
    cfg.thresholdMargin
  );

  json += ",\"consecutive\":";
  json += String(
    cfg.consecutive
  );

  json += ",\"burst\":";
  json += String(
    cfg.burstCycles
  );

  json += ",\"blank\":";
  json += String(
    cfg.blankUs
  );

  json += ",\"window\":";
  json += String(
    cfg.windowUs
  );

  json += ",\"noise\":";
  json += String(
    cfg.noiseUs
  );

  json += ",\"settle\":";
  json += String(
    cfg.settleMs
  );

  json += ",\"interval\":";
  json += String(
    cfg.scanIntervalMs
  );

  json += ",\"sound\":";
  json += String(
    cfg.soundCmPerUs,
    6
  );

  json += ",\"offset\":";
  json += String(
    cfg.tofOffsetUs
  );

  json += "}";

  // Summary
  json += ",\"summary\":{";

  json += "\"median\":";
  json += jsonFloat(
    medianDistance
  );

  json += ",\"medianRaw\":";
  json += jsonFloat(
    medianRaw
  );

  json += ",\"medianTof\":";
  json += jsonFloat(
    medianTOF,
    1
  );

  json += ",\"error\":";
  json += jsonFloat(
    errorCm
  );

  json += ",\"offsetEstimate\":";
  json += jsonFloat(
    offsetEstimateUs,
    1
  );

  uint8_t detectedCount = 0;

  for (uint8_t i = 0; i < 8; i++) {
    if (lastEcho[i].detected) {
      detectedCount++;
    }
  }

  json += ",\"detected\":";
  json += String(
    detectedCount
  );

  json += "}";

  // TX array
  json += ",\"tx\":[";

  for (uint8_t i = 0; i < 8; i++) {

    if (i > 0) {
      json += ",";
    }

    json += "{";

    json += "\"id\":";
    json += String(i + 1);

    json += ",\"detected\":";
    json += lastEcho[i].detected
      ? "true"
      : "false";

    json += ",\"noiseAvg\":";
    json += String(
      lastNoise[i].average,
      2
    );

    json += ",\"noiseMax\":";
    json += String(
      lastNoise[i].maximum
    );

    json += ",\"threshold\":";
    json += String(
      lastNoise[i].threshold
    );

    json += ",\"peak\":";
    json += String(
      lastEcho[i].peakADC
    );

    json += ",\"peakV\":";
    json += String(
      lastEcho[i].peakVoltage,
      4
    );

    json += ",\"tof\":";

    if (lastEcho[i].detected) {
      json += String(
        lastEcho[i].firstCrossUs
      );
    } else {
      json += "null";
    }

    json += ",\"peakTime\":";
    json += String(
      lastEcho[i].peakTimeUs
    );

    json += ",\"rawDistance\":";
    json += jsonFloat(
      lastEcho[i].rawDistanceCm
    );

    json += ",\"distance\":";
    json += jsonFloat(
      lastEcho[i].calibratedDistanceCm
    );

    json += "}";
  }

  json += "]";

  json += "}";

  return json;
}

// ============================================================
// HTML
// ============================================================

const char PAGE[] PROGMEM = R"rawliteral(
<!doctype html>
<html>
<head>
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Ultrasonic Calibration</title>

<style>
body{
  font-family:Arial,sans-serif;
  background:#111827;
  color:#e5e7eb;
  margin:0;
  padding:18px
}
h1{margin:0 0 5px}
.small{color:#9ca3af}
.grid{
  display:grid;
  grid-template-columns:repeat(auto-fit,minmax(180px,1fr));
  gap:10px
}
.card{
  background:#1f2937;
  border:1px solid #374151;
  border-radius:10px;
  padding:14px;
  margin-top:12px
}
input,select{
  width:100%;
  box-sizing:border-box;
  padding:8px;
  margin-top:4px;
  background:#111827;
  color:white;
  border:1px solid #4b5563;
  border-radius:5px
}
button{
  padding:10px 15px;
  margin:5px;
  border:0;
  border-radius:6px;
  cursor:pointer;
  font-weight:bold
}
.green{background:#10b981}
.blue{background:#3b82f6;color:white}
.red{background:#ef4444;color:white}
.gray{background:#4b5563;color:white}
.big{
  font-size:34px;
  font-weight:bold
}
.good{color:#34d399}
.bad{color:#f87171}
table{
  width:100%;
  border-collapse:collapse;
  margin-top:10px
}
th,td{
  border-bottom:1px solid #374151;
  padding:7px;
  text-align:center;
  font-size:13px
}
th{color:#93c5fd}
pre{
  overflow:auto;
  background:#030712;
  padding:10px;
  border-radius:6px
}
</style>
</head>

<body>

<h1>8-TX Ultrasonic Calibration</h1>
<div class="small">
ESP32 laboratory calibration dashboard
</div>

<div class="card">

<div class="grid">

<div>
Known distance (cm)
<input id="known" type="number" step="0.1">
</div>

<div>
Threshold margin (ADC)
<input id="threshold" type="number">
</div>

<div>
Consecutive samples
<input id="consecutive" type="number">
</div>

<div>
Burst cycles
<input id="burst" type="number">
</div>

<div>
RX blanking (us)
<input id="blank" type="number">
</div>

<div>
RX window (us)
<input id="window" type="number">
</div>

<div>
Noise measurement (us)
<input id="noise" type="number">
</div>

<div>
TX settle (ms)
<input id="settle" type="number">
</div>

<div>
Continuous interval (ms)
<input id="interval" type="number">
</div>

<div>
Sound speed (cm/us)
<input id="sound" type="number" step="0.000001">
</div>

<div>
TOF calibration offset (us)
<input id="offset" type="number">
</div>

<div>
ADC mode
<select id="mode">
<option value="1">Pseudo differential</option>
<option value="0">Single ended</option>
</select>
</div>

</div>

<br>

<button class="blue" onclick="saveSettings()">
Save settings
</button>

<button class="green" onclick="scanOnce()">
Scan once
</button>

<button class="green" onclick="startContinuous()">
Continuous ON
</button>

<button class="red" onclick="stopContinuous()">
Continuous OFF
</button>

<button class="gray" onclick="clearLog()">
Clear CSV log
</button>

<button class="gray" onclick="downloadCSV()">
Download CSV
</button>

</div>


<div class="card">

<div class="grid">

<div>
<div class="small">Median calibrated distance</div>
<div class="big" id="median">--</div>
</div>

<div>
<div class="small">Raw median distance</div>
<div class="big" id="medianRaw">--</div>
</div>

<div>
<div class="small">Known-distance error</div>
<div class="big" id="error">--</div>
</div>

<div>
<div class="small">Median first crossing</div>
<div class="big" id="medianTof">--</div>
</div>

<div>
<div class="small">Current offset estimate</div>
<div class="big" id="offsetEstimate">--</div>
</div>

<div>
<div class="small">Detection</div>
<div class="big" id="detected">0/8</div>
</div>

</div>

<div class="small">
Offset estimate is diagnostic only. Collect multiple known distances
before deciding the final TOF correction.
</div>

</div>


<div class="card">

<h3>TX1-TX8 results</h3>

<table>

<thead>
<tr>
<th>TX</th>
<th>Status</th>
<th>Noise</th>
<th>Threshold</th>
<th>Peak ADC</th>
<th>Peak V</th>
<th>First TOF us</th>
<th>Peak us</th>
<th>Raw cm</th>
<th>Cal cm</th>
<th>Error cm</th>
</tr>
</thead>

<tbody id="results">
</tbody>

</table>

</div>


<div class="card">

<div>
Scan ID:
<span id="scanId">0</span>

&nbsp;&nbsp;

Mode:
<span id="adcMode">--</span>

&nbsp;&nbsp;

Status:
<span id="status">Idle</span>
</div>

<div>
Logged CSV rows:
<span id="logCount">0</span>
</div>

</div>


<script>

let firstLoad = true;
let lastLoggedScan = -1;
let csvRows = [];

csvRows.push(
"ScanID,Known_cm,TX,ADC_Mode,NoiseAvg,NoiseMax,Threshold,PeakADC,PeakV,FirstCross_us,PeakTime_us,RawDistance_cm,CalDistance_cm,Error_cm"
);

function val(id){
  return document.getElementById(id).value;
}

async function saveSettings(){

  const q = new URLSearchParams();

  q.set("known", val("known"));
  q.set("threshold", val("threshold"));
  q.set("consecutive", val("consecutive"));
  q.set("burst", val("burst"));
  q.set("blank", val("blank"));
  q.set("window", val("window"));
  q.set("noise", val("noise"));
  q.set("settle", val("settle"));
  q.set("interval", val("interval"));
  q.set("sound", val("sound"));
  q.set("offset", val("offset"));
  q.set("mode", val("mode"));

  await fetch("/api/set?" + q.toString());

  await refresh();
}

async function scanOnce(){

  document.getElementById("status").textContent =
    "Scanning...";

  await fetch("/api/scan");

  await refresh();
}

async function startContinuous(){

  await fetch("/api/continuous?on=1");

  await refresh();
}

async function stopContinuous(){

  await fetch("/api/continuous?on=0");

  await refresh();
}

function showNumber(v, digits, suffix){

  if(v === null || v === undefined){
    return "--";
  }

  return Number(v).toFixed(digits) + suffix;
}

function applySettings(s){

  document.getElementById("known").value =
    s.known;

  document.getElementById("threshold").value =
    s.threshold;

  document.getElementById("consecutive").value =
    s.consecutive;

  document.getElementById("burst").value =
    s.burst;

  document.getElementById("blank").value =
    s.blank;

  document.getElementById("window").value =
    s.window;

  document.getElementById("noise").value =
    s.noise;

  document.getElementById("settle").value =
    s.settle;

  document.getElementById("interval").value =
    s.interval;

  document.getElementById("sound").value =
    s.sound;

  document.getElementById("offset").value =
    s.offset;
}

function logScan(d){

  if(d.scanId === 0){
    return;
  }

  if(d.scanId === lastLoggedScan){
    return;
  }

  lastLoggedScan = d.scanId;

  d.tx.forEach(t => {

    let err = "";

    if(t.distance !== null){
      err =
        (t.distance - d.settings.known)
        .toFixed(3);
    }

    csvRows.push([
      d.scanId,
      d.settings.known,
      t.id,
      d.mode,
      t.noiseAvg,
      t.noiseMax,
      t.threshold,
      t.peak,
      t.peakV,
      t.tof === null ? "" : t.tof,
      t.peakTime,
      t.rawDistance === null ? "" : t.rawDistance,
      t.distance === null ? "" : t.distance,
      err
    ].join(","));

  });

  document.getElementById("logCount").textContent =
    csvRows.length - 1;
}

function clearLog(){

  csvRows = [
  "ScanID,Known_cm,TX,ADC_Mode,NoiseAvg,NoiseMax,Threshold,PeakADC,PeakV,FirstCross_us,PeakTime_us,RawDistance_cm,CalDistance_cm,Error_cm"
  ];

  lastLoggedScan = -1;

  document.getElementById("logCount").textContent =
    "0";
}

function downloadCSV(){

  const blob =
    new Blob(
      [csvRows.join("\n")],
      {type:"text/csv"}
    );

  const url =
    URL.createObjectURL(blob);

  const a =
    document.createElement("a");

  a.href = url;

  a.download =
    "ultrasonic_calibration.csv";

  document.body.appendChild(a);

  a.click();

  a.remove();

  URL.revokeObjectURL(url);
}

async function refresh(){

  try{

    const response =
      await fetch("/api/data");

    const d =
      await response.json();

    document.getElementById("scanId").textContent =
      d.scanId;

    document.getElementById("adcMode").textContent =
      d.mode;

    document.getElementById("status").textContent =
      d.scanning
        ?
      "Scanning"
        :
      (
        d.continuous
          ?
        "Continuous"
          :
        "Idle"
      );

    if(firstLoad){

      applySettings(d.settings);

      document.getElementById("mode").value =
        d.mode === "PSEUDO-DIFF"
          ?
        "1"
          :
        "0";

      firstLoad = false;
    }

    document.getElementById("median").textContent =
      showNumber(
        d.summary.median,
        2,
        " cm"
      );

    document.getElementById("medianRaw").textContent =
      showNumber(
        d.summary.medianRaw,
        2,
        " cm"
      );

    document.getElementById("error").textContent =
      showNumber(
        d.summary.error,
        2,
        " cm"
      );

    document.getElementById("medianTof").textContent =
      showNumber(
        d.summary.medianTof,
        0,
        " us"
      );

    document.getElementById("offsetEstimate").textContent =
      showNumber(
        d.summary.offsetEstimate,
        0,
        " us"
      );

    document.getElementById("detected").textContent =
      d.summary.detected + "/8";

    let html = "";

    d.tx.forEach(t => {

      let error = null;

      if(t.distance !== null){
        error =
          t.distance -
          d.settings.known;
      }

      html += "<tr>";

      html +=
        "<td>TX" + t.id + "</td>";

      html +=
        "<td class='" +
        (t.detected ? "good" : "bad") +
        "'>" +
        (t.detected ? "ECHO" : "NO ECHO") +
        "</td>";

      html +=
        "<td>" +
        Number(t.noiseAvg).toFixed(2) +
        " / " +
        t.noiseMax +
        "</td>";

      html +=
        "<td>" +
        t.threshold +
        "</td>";

      html +=
        "<td>" +
        t.peak +
        "</td>";

      html +=
        "<td>" +
        Number(t.peakV).toFixed(3) +
        "</td>";

      html +=
        "<td>" +
        (t.tof === null ? "--" : t.tof) +
        "</td>";

      html +=
        "<td>" +
        t.peakTime +
        "</td>";

      html +=
        "<td>" +
        (
          t.rawDistance === null
            ?
          "--"
            :
          Number(t.rawDistance).toFixed(2)
        ) +
        "</td>";

      html +=
        "<td>" +
        (
          t.distance === null
            ?
          "--"
            :
          Number(t.distance).toFixed(2)
        ) +
        "</td>";

      html +=
        "<td>" +
        (
          error === null
            ?
          "--"
            :
          error.toFixed(2)
        ) +
        "</td>";

      html += "</tr>";
    });

    document.getElementById("results").innerHTML =
      html;

    logScan(d);

  }
  catch(e){

    document.getElementById("status").textContent =
      "Connection error";
  }
}

setInterval(refresh, 600);

refresh();

</script>

</body>
</html>
)rawliteral";

// ============================================================
// WEB ROUTES
// ============================================================

void handleRoot() {
  server.send_P(
    200,
    "text/html",
    PAGE
  );
}

void handleData() {
  server.send(
    200,
    "application/json",
    createDataJSON()
  );
}

void handleScan() {
  scanAll();

  server.send(
    200,
    "application/json",
    createDataJSON()
  );
}

void handleContinuous() {

  if (server.hasArg("on")) {
    continuousMode =
      server.arg("on").toInt() == 1;
  }

  server.send(
    200,
    "text/plain",
    continuousMode
      ?
    "ON"
      :
    "OFF"
  );
}

void handleSet() {

  if (server.hasArg("known")) {
    cfg.knownDistanceCm =
      server.arg("known").toFloat();
  }

  if (server.hasArg("threshold")) {
    cfg.thresholdMargin =
      constrain(
        server.arg("threshold").toInt(),
        0,
        500
      );
  }

  if (server.hasArg("consecutive")) {
    cfg.consecutive =
      constrain(
        server.arg("consecutive").toInt(),
        1,
        10
      );
  }

  if (server.hasArg("burst")) {
    cfg.burstCycles =
      constrain(
        server.arg("burst").toInt(),
        1,
        32
      );
  }

  if (server.hasArg("blank")) {

  int32_t blankValue =
    server.arg("blank").toInt();

  if (blankValue < 0) {
    blankValue = 0;
  }

  if (blankValue > 25000) {
    blankValue = 25000;
  }

  cfg.blankUs =
    (uint32_t)blankValue;
}

  if (server.hasArg("window")) {
    cfg.windowUs =
      constrain(
        server.arg("window").toInt(),
        1000,
        50000
      );
  }

  if (server.hasArg("noise")) {
    cfg.noiseUs =
      constrain(
        server.arg("noise").toInt(),
        500,
        20000
      );
  }

  if (server.hasArg("settle")) {
    cfg.settleMs =
      constrain(
        server.arg("settle").toInt(),
        1,
        500
      );
  }

  if (server.hasArg("interval")) {
    cfg.scanIntervalMs =
      constrain(
        server.arg("interval").toInt(),
        500,
        10000
      );
  }

  if (server.hasArg("sound")) {

    float value =
      server.arg("sound").toFloat();

    if (
      value > 0.02f &&
      value < 0.05f
    ) {
      cfg.soundCmPerUs =
        value;
    }
  }

  if (server.hasArg("offset")) {
    cfg.tofOffsetUs =
      server.arg("offset").toInt();
  }

  if (server.hasArg("mode")) {
    cfg.pseudoDifferential =
      server.arg("mode").toInt() == 1;
  }

  saveSettings();

  server.send(
    200,
    "text/plain",
    "SAVED"
  );
}

// ============================================================
// WIFI
// ============================================================

void startWiFi() {
  WiFi.mode(WIFI_STA);

  // Avoid power-save latency during measurement work.
  WiFi.setSleep(false);

  WiFi.begin(
    WIFI_SSID,
    WIFI_PASSWORD
  );

  Serial.print(
    "Connecting to "
  );

  Serial.println(
    WIFI_SSID
  );

  uint32_t startMs =
    millis();

  while (
    WiFi.status() != WL_CONNECTED
    &&
    millis() - startMs < 20000
  ) {
    delay(300);
    Serial.print(".");
  }

  Serial.println();

  if (
    WiFi.status() == WL_CONNECTED
  ) {

    Serial.println(
      "WiFi connected"
    );

    Serial.print(
      "IP address: "
    );

    Serial.println(
      WiFi.localIP()
    );

    if (
      MDNS.begin("ultrasonic")
    ) {
      Serial.println(
        "Try: http://ultrasonic.local"
      );
    }

  }
  else {

    Serial.println(
      "Could not connect to Saad."
    );

    Serial.println(
      "Starting fallback AP..."
    );

    WiFi.mode(WIFI_AP);

    WiFi.softAP(
      "Ultrasonic-Cal",
      "12345678"
    );

    Serial.print(
      "Fallback IP: "
    );

    Serial.println(
      WiFi.softAPIP()
    );
  }
}

// ============================================================
// SETUP
// ============================================================

void setup() {
  Serial.begin(115200);

  delay(1000);

  loadSettings();

  // TX
  for (uint8_t i = 0; i < 8; i++) {
    pinMode(
      TX_PINS[i],
      OUTPUT
    );

    digitalWrite(
      TX_PINS[i],
      LOW
    );
  }

  allTxOff();

  // MCP3008
  pinMode(
    MCP_CS,
    OUTPUT
  );

  digitalWrite(
    MCP_CS,
    HIGH
  );

  SPI.begin(
    MCP_SCK,
    MCP_MISO,
    MCP_MOSI,
    MCP_CS
  );

  startWiFi();

  // Web routes
  server.on(
    "/",
    handleRoot
  );

  server.on(
    "/api/data",
    handleData
  );

  server.on(
    "/api/scan",
    handleScan
  );

  server.on(
    "/api/set",
    handleSet
  );

  server.on(
    "/api/continuous",
    handleContinuous
  );

  server.begin();

  Serial.println();
  Serial.println(
    "Ultrasonic calibration server ready."
  );

  Serial.print(
    "Default ADC mode: "
  );

  Serial.println(
    cfg.pseudoDifferential
      ?
    "PSEUDO-DIFFERENTIAL"
      :
    "SINGLE-ENDED"
  );
}

// ============================================================
// LOOP
// ============================================================

void loop() {
  server.handleClient();

  if (
    continuousMode
    &&
    !scanning
    &&
    (
      millis()
      -
      lastContinuousScanMs
      >=
      cfg.scanIntervalMs
    )
  ) {

    lastContinuousScanMs =
      millis();

    scanAll();
  }

  delay(1);
}