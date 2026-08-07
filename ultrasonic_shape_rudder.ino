/*
  ESP32 8-TX TRUE-TIME-DELAY BEAMFORMING + MCP3008 WEB TEST
  Arduino-ESP32 3.3.11 compile fix
  ----------------------------------------------------------
  Purpose:
    - Fire all 8 ultrasonic TX elements with calculated delays.
    - Measure the single RX envelope through MCP3008.
    - Compare PeakADC versus commanded steering angle.
    - Run an automatic sweep: -15,-10,-5,0,+5,+10,+15 degrees.
    - Store results in ESP32 RAM and download a CSV.

  Geometry supplied by the user:
    Q1..Q8 X positions = 24,41,58,75,92,109,126,143 mm
    Uniform pitch = 17 mm
    Centred X coordinates = -59.5 ... +59.5 mm

  IMPORTANT:
    1) This is transmit beam steering with one RX, not receive beamforming.
    2) With 17 mm spacing at 40 kHz, grating lobes are unavoidable.
    3) For the first experiment, compare PeakADC. Do not judge steering
       from the printed delay values alone.
    4) Keep the array, RX, tripod and target completely stationary.
*/

// Forward declarations must appear before the first function definition.
// Arduino's .ino preprocessor may generate function prototypes near the top
// of the sketch; these declarations make custom types visible there.
struct Settings;
struct NoiseStats;
struct EchoResult;
struct GpioEvent;
struct BeamPlan;
struct BeamMeasurement;
struct LogRow;

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <SPI.h>
#include <Preferences.h>
#include <algorithm>
#include <math.h>

#if __has_include("esp_cpu.h")
  #include "esp_cpu.h"
  static inline uint32_t cpuCycleCount() {
    return static_cast<uint32_t>(esp_cpu_get_cycle_count());
  }
#else
  #include "xtensa/core-macros.h"
  static inline uint32_t cpuCycleCount() {
    return static_cast<uint32_t>(XTHAL_GET_CCOUNT());
  }
#endif

#include "soc/gpio_struct.h"
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"

// ============================================================
// WIFI - copied from the uploaded calibration sketch
// ============================================================

const char* WIFI_SSID = "YOUR_WIFI_SSID";
const char* WIFI_PASSWORD = "YOUR_WIFI_PASSWORD";

WebServer server(80);
Preferences preferences;

// ============================================================
// HARDWARE
// ============================================================

static constexpr uint8_t N_TX = 8;

// Physical left-to-right order Q1 -> Q8.
static constexpr uint8_t TX_PINS[N_TX] = {
  13, 12, 14, 27, 26, 25, 33, 32
};

static constexpr float TX_X_MM[N_TX] = {
  -59.5f, -42.5f, -25.5f, -8.5f,
    8.5f,  25.5f,  42.5f, 59.5f
};

// Keep zero until per-channel timing is measured.
static constexpr float CHANNEL_TRIM_US[N_TX] = {
  0.0f, 0.0f, 0.0f, 0.0f,
  0.0f, 0.0f, 0.0f, 0.0f
};

// MCP3008
static constexpr uint8_t MCP_CS   = 5;
static constexpr uint8_t MCP_SCK  = 18;
static constexpr uint8_t MCP_MISO = 19;
static constexpr uint8_t MCP_MOSI = 23;

static constexpr float ADC_VREF = 3.300f;
static constexpr uint16_t ADC_MAX = 1023;
static constexpr uint32_t MCP_SPI_HZ = 2000000;

SPISettings mcpSPI(MCP_SPI_HZ, MSBFIRST, SPI_MODE0);

// Beam constants
static constexpr float TX_FREQUENCY_HZ = 40000.0f;
static constexpr float PERIOD_US = 25.0f;
static constexpr float HIGH_US = 12.5f;
static constexpr float SOUND_SPEED_MM_PER_US = 0.34675f;
static constexpr float START_GUARD_US = 5.0f;
static constexpr uint8_t MAX_BURST_CYCLES = 32;

// ============================================================
// SETTINGS
// ============================================================

struct Settings {
  uint16_t thresholdMargin = 6;
  uint8_t consecutive = 2;
  uint8_t burstCycles = 32;

  uint32_t blankUs = 300;
  uint32_t windowUs = 12000;
  uint32_t noiseUs = 2500;

  uint16_t settleMs = 30;
  uint16_t scanIntervalMs = 800;

  float soundCmPerUs = 0.034675f;
  int32_t tofOffsetUs = 0;
  float knownDistanceCm = 50.0f;

  bool pseudoDifferential = true;

  float beamAngleDeg = 0.0f;
  uint8_t sweepRepeats = 10;
};

Settings cfg;

// ============================================================
// DATA TYPES
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

  // Relative to the earliest TX edge.
  uint32_t firstCrossUs;
  uint32_t peakTimeUs;

  // First crossing referenced approximately to the array-centre wavefront.
  float beamReferencedTofUs;
  float rawDistanceCm;
  float calibratedDistanceCm;
  uint32_t samples;
};

struct GpioEvent {
  uint32_t tick;
  uint64_t setMask;
  uint64_t clearMask;
};

struct BeamPlan {
  float angleDeg;
  float delayUs[N_TX];
  float wavefrontReferenceDelayUs;
  float maximumDelayUs;
  uint32_t durationTicks;
  size_t eventCount;
};

struct BeamMeasurement {
  uint32_t scanId;
  float angleDeg;
  NoiseStats noise;
  EchoResult echo;
  float beamReferenceDelayUs;
  float maximumChannelDelayUs;
};

struct LogRow {
  uint32_t scanId;
  float angleDeg;
  float knownCm;
  bool differential;
  float noiseAverage;
  uint16_t noiseMaximum;
  uint16_t threshold;
  uint16_t peakADC;
  float peakVoltage;
  bool detected;
  uint32_t firstCrossUs;
  float beamReferenceDelayUs;
  float beamTofUs;
  uint32_t peakTimeUs;
  float rawDistanceCm;
  float calibratedDistanceCm;
};

// Explicit prototypes after custom type declarations prevent Arduino's
// automatic prototype generator from placing BeamPlan prototypes too early.
static void loadSettings();
static void saveSettings();
static void allTxOff();
static inline void gpioSetMask64(uint64_t mask);
static inline void gpioClearMask64(uint64_t mask);
static inline uint32_t usToTicks(float us);
static inline bool tickNotReached(uint32_t now, uint32_t target);
static void addEvent(uint32_t tick, uint64_t setMask, uint64_t clearMask, size_t &count);
static bool prepareBeam(float angleDeg, BeamPlan &plan);
static uint32_t firePreparedBeam(const BeamPlan &plan);
static NoiseStats measureNoise();
static EchoResult captureEcho(uint32_t txReferenceUs, const BeamPlan &plan, uint16_t threshold);
static bool performBeamScan(float angleDeg, bool appendToLog = true);
static String createDataJSON();
static void startWiFi();

// ============================================================
// GLOBAL STATE
// ============================================================

static constexpr size_t MAX_EVENTS = N_TX * MAX_BURST_CYCLES * 2;
static GpioEvent events[MAX_EVENTS];
static BeamPlan currentPlan;
static BeamMeasurement lastMeasurement = {};

static portMUX_TYPE beamMux = portMUX_INITIALIZER_UNLOCKED;
static uint64_t allTxMask = 0;
static uint32_t ticksPerUs = 240;

static uint32_t scanId = 0;
static bool scanning = false;
static bool continuousMode = false;
static uint32_t lastContinuousScanMs = 0;

static constexpr size_t MAX_LOG_ROWS = 700;
static LogRow logRows[MAX_LOG_ROWS];
static size_t logCount = 0;

// ============================================================
// SETTINGS STORAGE
// ============================================================

static void loadSettings() {
  preferences.begin("ultra-beam", false);

  cfg.thresholdMargin = preferences.getUShort("thr", 6);
  cfg.consecutive = preferences.getUChar("cons", 2);
  cfg.burstCycles = preferences.getUChar("burst", 32);
  cfg.blankUs = preferences.getULong("blank", 300);
  cfg.windowUs = preferences.getULong("window", 12000);
  cfg.noiseUs = preferences.getULong("noise", 2500);
  cfg.settleMs = preferences.getUShort("settle", 30);
  cfg.scanIntervalMs = preferences.getUShort("interval", 800);
  cfg.soundCmPerUs = preferences.getFloat("sound", 0.034675f);
  cfg.tofOffsetUs = preferences.getInt("offset", 0);
  cfg.knownDistanceCm = preferences.getFloat("known", 50.0f);
  cfg.pseudoDifferential = preferences.getBool("diff", true);
  cfg.beamAngleDeg = preferences.getFloat("angle", 0.0f);
  cfg.sweepRepeats = preferences.getUChar("repeats", 10);

  cfg.burstCycles = constrain(cfg.burstCycles, (uint8_t)1, MAX_BURST_CYCLES);
  cfg.sweepRepeats = constrain(cfg.sweepRepeats, (uint8_t)1, (uint8_t)30);
}

static void saveSettings() {
  preferences.putUShort("thr", cfg.thresholdMargin);
  preferences.putUChar("cons", cfg.consecutive);
  preferences.putUChar("burst", cfg.burstCycles);
  preferences.putULong("blank", cfg.blankUs);
  preferences.putULong("window", cfg.windowUs);
  preferences.putULong("noise", cfg.noiseUs);
  preferences.putUShort("settle", cfg.settleMs);
  preferences.putUShort("interval", cfg.scanIntervalMs);
  preferences.putFloat("sound", cfg.soundCmPerUs);
  preferences.putInt("offset", cfg.tofOffsetUs);
  preferences.putFloat("known", cfg.knownDistanceCm);
  preferences.putBool("diff", cfg.pseudoDifferential);
  preferences.putFloat("angle", cfg.beamAngleDeg);
  preferences.putUChar("repeats", cfg.sweepRepeats);
}

// ============================================================
// FAST GPIO / BEAM TIMING
// ============================================================

static void allTxOff() {
  gpioClearMask64(allTxMask);
}

static inline void gpioSetMask64(uint64_t mask) {
  const uint32_t low = static_cast<uint32_t>(mask);
  const uint32_t high = static_cast<uint32_t>(mask >> 32);
  if (low) GPIO.out_w1ts = low;
  if (high) GPIO.out1_w1ts.val = high;
}

static inline void gpioClearMask64(uint64_t mask) {
  const uint32_t low = static_cast<uint32_t>(mask);
  const uint32_t high = static_cast<uint32_t>(mask >> 32);
  if (low) GPIO.out_w1tc = low;
  if (high) GPIO.out1_w1tc.val = high;
}

static inline uint32_t usToTicks(float us) {
  return static_cast<uint32_t>(lroundf(us * static_cast<float>(ticksPerUs)));
}

static inline bool tickNotReached(uint32_t now, uint32_t target) {
  return static_cast<int32_t>(now - target) < 0;
}

static void addEvent(uint32_t tick, uint64_t setMask, uint64_t clearMask, size_t &count) {
  if (count >= MAX_EVENTS) return;
  events[count++] = {tick, setMask, clearMask};
}

static bool prepareBeam(float angleDeg, BeamPlan &plan) {
  if (!isfinite(angleDeg) || angleDeg < -30.0f || angleDeg > 30.0f) {
    return false;
  }

  const float thetaRad = angleDeg * static_cast<float>(PI) / 180.0f;
  const float sineTheta = sinf(thetaRad);

  float rawDelayUs[N_TX];
  float minimumRawDelay = 1.0e9f;

  for (uint8_t i = 0; i < N_TX; ++i) {
    rawDelayUs[i] =
      (TX_X_MM[i] * sineTheta / SOUND_SPEED_MM_PER_US)
      + CHANNEL_TRIM_US[i];
    minimumRawDelay = fminf(minimumRawDelay, rawDelayUs[i]);
  }

  plan.angleDeg = angleDeg;
  plan.wavefrontReferenceDelayUs = -minimumRawDelay;
  plan.maximumDelayUs = 0.0f;

  for (uint8_t i = 0; i < N_TX; ++i) {
    plan.delayUs[i] = rawDelayUs[i] - minimumRawDelay;
    plan.maximumDelayUs = fmaxf(plan.maximumDelayUs, plan.delayUs[i]);
  }

  size_t count = 0;
  const uint32_t periodTicks = usToTicks(PERIOD_US);
  const uint32_t highTicks = usToTicks(HIGH_US);
  uint32_t maximumEndTick = 0;

  for (uint8_t i = 0; i < N_TX; ++i) {
    const uint64_t pinMask = (1ULL << TX_PINS[i]);
    const uint32_t channelDelayTicks = usToTicks(plan.delayUs[i]);

    for (uint8_t cycle = 0; cycle < cfg.burstCycles; ++cycle) {
      const uint32_t riseTick = channelDelayTicks + cycle * periodTicks;
      const uint32_t fallTick = riseTick + highTicks;
      addEvent(riseTick, pinMask, 0, count);
      addEvent(fallTick, 0, pinMask, count);
      maximumEndTick = max(maximumEndTick, fallTick);
    }
  }

  std::sort(events, events + count,
    [](const GpioEvent &a, const GpioEvent &b) {
      return a.tick < b.tick;
    }
  );

  size_t mergedCount = 0;
  for (size_t i = 0; i < count; ++i) {
    if (mergedCount > 0 && events[mergedCount - 1].tick == events[i].tick) {
      events[mergedCount - 1].setMask |= events[i].setMask;
      events[mergedCount - 1].clearMask |= events[i].clearMask;
    } else {
      events[mergedCount++] = events[i];
    }
  }

  plan.eventCount = mergedCount;
  plan.durationTicks = maximumEndTick;
  return true;
}

static uint32_t firePreparedBeam(const BeamPlan &plan) {
  allTxOff();

  const uint32_t guardTicks = usToTicks(START_GUARD_US);
  uint32_t referenceUs;

  portENTER_CRITICAL(&beamMux);

  const uint32_t t0 = cpuCycleCount() + guardTicks;
  referenceUs = micros() + static_cast<uint32_t>(START_GUARD_US);

  for (size_t i = 0; i < plan.eventCount; ++i) {
    const uint32_t target = t0 + events[i].tick;
    while (tickNotReached(cpuCycleCount(), target)) {
      // Intentional sub-microsecond timing wait.
    }

    if (events[i].clearMask) gpioClearMask64(events[i].clearMask);
    if (events[i].setMask) gpioSetMask64(events[i].setMask);
  }

  allTxOff();
  portEXIT_CRITICAL(&beamMux);

  return referenceUs;
}

// ============================================================
// MCP3008
// ============================================================

static inline uint16_t mcpTransfer(uint8_t commandByte) {
  digitalWrite(MCP_CS, LOW);
  SPI.transfer(0x01);
  const uint8_t highByte = SPI.transfer(commandByte);
  const uint8_t lowByte = SPI.transfer(0x00);
  digitalWrite(MCP_CS, HIGH);

  return ((uint16_t)(highByte & 0x03) << 8) | lowByte;
}

static inline uint16_t readSingleCH0() {
  return mcpTransfer(0x80);
}

static inline uint16_t readDiffCH0CH1() {
  return mcpTransfer(0x00);
}

static inline uint16_t readEnvelopeADC() {
  return cfg.pseudoDifferential ? readDiffCH0CH1() : readSingleCH0();
}

static float adcToVoltage(uint16_t adc) {
  return static_cast<float>(adc) * ADC_VREF / 1023.0f;
}

// ============================================================
// NOISE / ECHO
// ============================================================

static NoiseStats measureNoise() {
  NoiseStats n = {};
  n.minimum = ADC_MAX;

  uint64_t sum = 0;
  const uint32_t startUs = micros();

  SPI.beginTransaction(mcpSPI);

  while ((uint32_t)(micros() - startUs) < cfg.noiseUs) {
    const uint16_t adc = readEnvelopeADC();
    n.minimum = min(n.minimum, adc);
    n.maximum = max(n.maximum, adc);
    sum += adc;
    n.samples++;
  }

  SPI.endTransaction();

  if (n.samples > 0) {
    n.average = static_cast<float>(sum) / static_cast<float>(n.samples);
  }

  uint32_t thresholdValue = static_cast<uint32_t>(n.maximum) + cfg.thresholdMargin;
  thresholdValue = min(thresholdValue, static_cast<uint32_t>(ADC_MAX));
  n.threshold = static_cast<uint16_t>(thresholdValue);
  return n;
}

static EchoResult captureEcho(
  uint32_t txReferenceUs,
  const BeamPlan &plan,
  uint16_t threshold
) {
  EchoResult result = {};
  result.rawDistanceCm = NAN;
  result.calibratedDistanceCm = NAN;
  result.beamReferencedTofUs = NAN;

  // Conservatively wait for the latest-starting element to complete all cycles.
  const uint32_t excitationEndUs = static_cast<uint32_t>(
    ceilf(plan.maximumDelayUs + cfg.burstCycles * PERIOD_US)
  );
  const uint32_t receiveStartUs = excitationEndUs + cfg.blankUs;

  while ((uint32_t)(micros() - txReferenceUs) < receiveStartUs) {
    // Intentional wait.
  }

  const uint32_t windowStartUs = micros();
  uint8_t consecutiveCount = 0;
  uint32_t crossingCandidateUs = 0;

  SPI.beginTransaction(mcpSPI);

  while ((uint32_t)(micros() - windowStartUs) < cfg.windowUs) {
    const uint32_t sampleTimeUs = micros();
    const uint16_t adc = readEnvelopeADC();
    result.samples++;

    if (adc > result.peakADC) {
      result.peakADC = adc;
      result.peakTimeUs = sampleTimeUs - txReferenceUs;
    }

    if (!result.detected) {
      if (adc >= threshold) {
        if (consecutiveCount == 0) {
          crossingCandidateUs = sampleTimeUs - txReferenceUs;
        }
        consecutiveCount++;

        if (consecutiveCount >= cfg.consecutive) {
          result.detected = true;
          result.firstCrossUs = crossingCandidateUs;
        }
      } else {
        consecutiveCount = 0;
      }
    }
  }

  SPI.endTransaction();

  result.peakVoltage = adcToVoltage(result.peakADC);

  if (result.detected) {
    result.beamReferencedTofUs =
      static_cast<float>(result.firstCrossUs) - plan.wavefrontReferenceDelayUs;

    if (result.beamReferencedTofUs < 0.0f) {
      result.beamReferencedTofUs = 0.0f;
    }

    result.rawDistanceCm =
      result.beamReferencedTofUs * cfg.soundCmPerUs / 2.0f;

    float calibratedTofUs = result.beamReferencedTofUs - cfg.tofOffsetUs;
    if (calibratedTofUs < 0.0f) calibratedTofUs = 0.0f;

    result.calibratedDistanceCm =
      calibratedTofUs * cfg.soundCmPerUs / 2.0f;
  }

  return result;
}

// ============================================================
// SCAN / LOG
// ============================================================

static void appendLog(const BeamMeasurement &m) {
  if (logCount >= MAX_LOG_ROWS) return;

  LogRow &r = logRows[logCount++];
  r.scanId = m.scanId;
  r.angleDeg = m.angleDeg;
  r.knownCm = cfg.knownDistanceCm;
  r.differential = cfg.pseudoDifferential;
  r.noiseAverage = m.noise.average;
  r.noiseMaximum = m.noise.maximum;
  r.threshold = m.noise.threshold;
  r.peakADC = m.echo.peakADC;
  r.peakVoltage = m.echo.peakVoltage;
  r.detected = m.echo.detected;
  r.firstCrossUs = m.echo.firstCrossUs;
  r.beamReferenceDelayUs = m.beamReferenceDelayUs;
  r.beamTofUs = m.echo.beamReferencedTofUs;
  r.peakTimeUs = m.echo.peakTimeUs;
  r.rawDistanceCm = m.echo.rawDistanceCm;
  r.calibratedDistanceCm = m.echo.calibratedDistanceCm;
}

static void printMeasurementCSV(const BeamMeasurement &m) {
  Serial.printf(
    "%lu,%.1f,%.1f,%s,%.2f,%u,%u,%u,%.4f,%s,",
    static_cast<unsigned long>(m.scanId),
    m.angleDeg,
    cfg.knownDistanceCm,
    cfg.pseudoDifferential ? "DIFF" : "SINGLE",
    m.noise.average,
    m.noise.maximum,
    m.noise.threshold,
    m.echo.peakADC,
    m.echo.peakVoltage,
    m.echo.detected ? "ECHO" : "NO_ECHO"
  );

  if (m.echo.detected) {
    Serial.printf(
      "%lu,%.3f,%.3f,%lu,%.3f,%.3f\n",
      static_cast<unsigned long>(m.echo.firstCrossUs),
      m.beamReferenceDelayUs,
      m.echo.beamReferencedTofUs,
      static_cast<unsigned long>(m.echo.peakTimeUs),
      m.echo.rawDistanceCm,
      m.echo.calibratedDistanceCm
    );
  } else {
    Serial.printf(
      "NA,%.3f,NA,%lu,NA,NA\n",
      m.beamReferenceDelayUs,
      static_cast<unsigned long>(m.echo.peakTimeUs)
    );
  }
}

static bool performBeamScan(float angleDeg, bool appendToLog) {
  if (!prepareBeam(angleDeg, currentPlan)) return false;

  allTxOff();
  delay(cfg.settleMs);

  lastMeasurement.noise = measureNoise();

  const uint32_t txReferenceUs = firePreparedBeam(currentPlan);
  lastMeasurement.echo = captureEcho(
    txReferenceUs,
    currentPlan,
    lastMeasurement.noise.threshold
  );

  scanId++;
  lastMeasurement.scanId = scanId;
  lastMeasurement.angleDeg = angleDeg;
  lastMeasurement.beamReferenceDelayUs = currentPlan.wavefrontReferenceDelayUs;
  lastMeasurement.maximumChannelDelayUs = currentPlan.maximumDelayUs;

  if (appendToLog) appendLog(lastMeasurement);
  printMeasurementCSV(lastMeasurement);
  return true;
}

// ============================================================
// JSON
// ============================================================

static String jsonFloat(float value, uint8_t decimals = 3) {
  if (isnan(value)) return "null";
  return String(value, static_cast<unsigned int>(decimals));
}

static String createDataJSON() {
  String json;
  json.reserve(2600);

  json += "{";
  json += "\"scanId\":" + String(scanId);
  json += ",\"scanning\":" + String(scanning ? "true" : "false");
  json += ",\"continuous\":" + String(continuousMode ? "true" : "false");
  json += ",\"logCount\":" + String(logCount);

  json += ",\"settings\":{";
  json += "\"known\":" + String(cfg.knownDistanceCm, 2);
  json += ",\"threshold\":" + String(cfg.thresholdMargin);
  json += ",\"consecutive\":" + String(cfg.consecutive);
  json += ",\"burst\":" + String(cfg.burstCycles);
  json += ",\"blank\":" + String(cfg.blankUs);
  json += ",\"window\":" + String(cfg.windowUs);
  json += ",\"noise\":" + String(cfg.noiseUs);
  json += ",\"settle\":" + String(cfg.settleMs);
  json += ",\"interval\":" + String(cfg.scanIntervalMs);
  json += ",\"sound\":" + String(cfg.soundCmPerUs, 6);
  json += ",\"offset\":" + String(cfg.tofOffsetUs);
  json += ",\"mode\":" + String(cfg.pseudoDifferential ? 1 : 0);
  json += ",\"angle\":" + String(cfg.beamAngleDeg, 1);
  json += ",\"repeats\":" + String(cfg.sweepRepeats);
  json += "}";

  json += ",\"result\":{";
  json += "\"angle\":" + String(lastMeasurement.angleDeg, 1);
  json += ",\"detected\":" + String(lastMeasurement.echo.detected ? "true" : "false");
  json += ",\"noiseAvg\":" + String(lastMeasurement.noise.average, 2);
  json += ",\"noiseMax\":" + String(lastMeasurement.noise.maximum);
  json += ",\"threshold\":" + String(lastMeasurement.noise.threshold);
  json += ",\"peak\":" + String(lastMeasurement.echo.peakADC);
  json += ",\"peakV\":" + String(lastMeasurement.echo.peakVoltage, 4);
  json += ",\"firstTof\":";
  json += lastMeasurement.echo.detected ? String(lastMeasurement.echo.firstCrossUs) : "null";
  json += ",\"beamRef\":" + String(lastMeasurement.beamReferenceDelayUs, 3);
  json += ",\"beamTof\":" + jsonFloat(lastMeasurement.echo.beamReferencedTofUs, 3);
  json += ",\"peakTime\":" + String(lastMeasurement.echo.peakTimeUs);
  json += ",\"rawDistance\":" + jsonFloat(lastMeasurement.echo.rawDistanceCm, 3);
  json += ",\"distance\":" + jsonFloat(lastMeasurement.echo.calibratedDistanceCm, 3);
  json += "}";

  json += "}";
  return json;
}

// ============================================================
// WEB PAGE
// ============================================================

const char PAGE[] PROGMEM = R"rawliteral(
<!doctype html>
<html>
<head>
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>8-TX Beamforming Test</title>
<style>
body{font-family:Arial,sans-serif;background:#111827;color:#e5e7eb;margin:0;padding:16px}
h1{margin:0 0 4px}.small{color:#9ca3af}.card{background:#1f2937;border:1px solid #374151;border-radius:10px;padding:14px;margin-top:12px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px}input,select{width:100%;box-sizing:border-box;padding:8px;margin-top:4px;background:#111827;color:white;border:1px solid #4b5563;border-radius:5px}button{padding:10px 14px;margin:5px;border:0;border-radius:6px;font-weight:bold;cursor:pointer}.blue{background:#3b82f6;color:white}.green{background:#10b981}.red{background:#ef4444;color:white}.gray{background:#4b5563;color:white}.orange{background:#f59e0b}.big{font-size:30px;font-weight:bold}.good{color:#34d399}.bad{color:#f87171}table{width:100%;border-collapse:collapse}th,td{border-bottom:1px solid #374151;padding:8px;text-align:center;font-size:13px}th{color:#93c5fd}
</style>
</head>
<body>
<h1>8-TX Beamforming Test</h1>
<div class="small">Main result: compare Peak ADC versus commanded angle.</div>

<div class="card">
<div class="grid">
<div>Beam angle (deg)<input id="angle" type="number" min="-15" max="15" step="1"></div>
<div>Sweep repeats/angle<input id="repeats" type="number" min="1" max="30"></div>
<div>Known distance (cm)<input id="known" type="number" step="0.1"></div>
<div>Threshold margin<input id="threshold" type="number"></div>
<div>Consecutive samples<input id="consecutive" type="number"></div>
<div>Burst cycles<input id="burst" type="number" min="1" max="32"></div>
<div>RX blanking (us)<input id="blank" type="number"></div>
<div>RX window (us)<input id="window" type="number"></div>
<div>Noise measurement (us)<input id="noise" type="number"></div>
<div>TX settle (ms)<input id="settle" type="number"></div>
<div>Continuous interval (ms)<input id="interval" type="number"></div>
<div>Sound speed (cm/us)<input id="sound" type="number" step="0.000001"></div>
<div>TOF offset (us)<input id="offset" type="number"></div>
<div>ADC mode<select id="mode"><option value="1">Pseudo differential</option><option value="0">Single ended</option></select></div>
</div>
<br>
<button class="blue" onclick="saveSettings()">Save settings</button>
<button class="green" onclick="scanOnce()">Beam scan once</button>
<button class="orange" onclick="runSweep()">Run -15&deg; to +15&deg; sweep</button>
<button class="green" onclick="continuous(1)">Continuous ON</button>
<button class="red" onclick="continuous(0)">Continuous OFF</button>
<button class="gray" onclick="clearLog()">Clear log</button>
<button class="gray" onclick="downloadCSV()">Download CSV</button>
</div>

<div class="card"><div class="grid">
<div><div class="small">Angle</div><div class="big" id="rAngle">--</div></div>
<div><div class="small">Peak ADC</div><div class="big" id="peak">--</div></div>
<div><div class="small">Peak voltage</div><div class="big" id="peakV">--</div></div>
<div><div class="small">Detection</div><div class="big" id="detected">--</div></div>
<div><div class="small">Beam-referenced TOF</div><div class="big" id="beamTof">--</div></div>
<div><div class="small">Distance</div><div class="big" id="distance">--</div></div>
</div></div>

<div class="card">
<table><thead><tr><th>Noise</th><th>Threshold</th><th>First TOF</th><th>Beam ref delay</th><th>Peak time</th><th>Raw cm</th></tr></thead>
<tbody><tr><td id="noiseResult">--</td><td id="thrResult">--</td><td id="firstTof">--</td><td id="beamRef">--</td><td id="peakTime">--</td><td id="rawDistance">--</td></tr></tbody></table>
</div>

<div class="card">Status: <span id="status">Idle</span> &nbsp; Scan ID: <span id="scanId">0</span> &nbsp; Logged rows: <span id="logCount">0</span></div>

<script>
let firstLoad=true;
function val(id){return document.getElementById(id).value;}
function show(v,d,s){return (v===null||v===undefined)?"--":Number(v).toFixed(d)+s;}
async function saveSettings(){
 const q=new URLSearchParams();
 ["angle","repeats","known","threshold","consecutive","burst","blank","window","noise","settle","interval","sound","offset","mode"].forEach(id=>q.set(id,val(id)));
 await fetch('/api/set?'+q.toString()); await refresh();
}
async function scanOnce(){document.getElementById('status').textContent='Scanning...';await saveSettings();await fetch('/api/scan');await refresh();}
async function runSweep(){document.getElementById('status').textContent='Running sweep...';await saveSettings();await fetch('/api/sweep');await refresh();}
async function continuous(on){await fetch('/api/continuous?on='+on);await refresh();}
async function clearLog(){await fetch('/api/clear');await refresh();}
function downloadCSV(){window.location='/api/csv';}
function applySettings(s){for(const k of ["angle","repeats","known","threshold","consecutive","burst","blank","window","noise","settle","interval","sound","offset","mode"]){document.getElementById(k).value=s[k];}}
async function refresh(){
 try{
  const d=await (await fetch('/api/data')).json();
  if(firstLoad){applySettings(d.settings);firstLoad=false;}
  document.getElementById('status').textContent=d.scanning?'Scanning':(d.continuous?'Continuous':'Idle');
  document.getElementById('scanId').textContent=d.scanId;
  document.getElementById('logCount').textContent=d.logCount;
  const r=d.result;
  document.getElementById('rAngle').textContent=show(r.angle,1,'\u00B0');
  document.getElementById('peak').textContent=r.peak;
  document.getElementById('peakV').textContent=show(r.peakV,3,' V');
  document.getElementById('detected').textContent=r.detected?'ECHO':'NO ECHO';
  document.getElementById('detected').className='big '+(r.detected?'good':'bad');
  document.getElementById('beamTof').textContent=show(r.beamTof,0,' us');
  document.getElementById('distance').textContent=show(r.distance,2,' cm');
  document.getElementById('noiseResult').textContent=Number(r.noiseAvg).toFixed(2)+' / '+r.noiseMax;
  document.getElementById('thrResult').textContent=r.threshold;
  document.getElementById('firstTof').textContent=r.firstTof===null?'--':r.firstTof+' us';
  document.getElementById('beamRef').textContent=show(r.beamRef,3,' us');
  document.getElementById('peakTime').textContent=r.peakTime+' us';
  document.getElementById('rawDistance').textContent=show(r.rawDistance,2,' cm');
 }catch(e){document.getElementById('status').textContent='Connection error';}
}
setInterval(refresh,700);refresh();
</script>
</body>
</html>
)rawliteral";

// ============================================================
// WEB ROUTES
// ============================================================

static void handleRoot() {
  server.send_P(200, "text/html", PAGE);
}

static void handleData() {
  server.send(200, "application/json", createDataJSON());
}

static void handleSet() {
  if (server.hasArg("angle")) cfg.beamAngleDeg = constrain(server.arg("angle").toFloat(), -15.0f, 15.0f);
  if (server.hasArg("repeats")) cfg.sweepRepeats = constrain(server.arg("repeats").toInt(), 1, 30);
  if (server.hasArg("known")) cfg.knownDistanceCm = server.arg("known").toFloat();
  if (server.hasArg("threshold")) cfg.thresholdMargin = constrain(server.arg("threshold").toInt(), 0, 500);
  if (server.hasArg("consecutive")) cfg.consecutive = constrain(server.arg("consecutive").toInt(), 1, 10);
  if (server.hasArg("burst")) cfg.burstCycles = constrain(server.arg("burst").toInt(), 1, 32);
  if (server.hasArg("blank")) cfg.blankUs = constrain(server.arg("blank").toInt(), 0, 25000);
  if (server.hasArg("window")) cfg.windowUs = constrain(server.arg("window").toInt(), 1000, 50000);
  if (server.hasArg("noise")) cfg.noiseUs = constrain(server.arg("noise").toInt(), 500, 20000);
  if (server.hasArg("settle")) cfg.settleMs = constrain(server.arg("settle").toInt(), 1, 500);
  if (server.hasArg("interval")) cfg.scanIntervalMs = constrain(server.arg("interval").toInt(), 500, 10000);
  if (server.hasArg("sound")) {
    const float value = server.arg("sound").toFloat();
    if (value > 0.02f && value < 0.05f) cfg.soundCmPerUs = value;
  }
  if (server.hasArg("offset")) cfg.tofOffsetUs = server.arg("offset").toInt();
  if (server.hasArg("mode")) cfg.pseudoDifferential = server.arg("mode").toInt() == 1;

  saveSettings();
  server.send(200, "text/plain", "SAVED");
}

static void handleScan() {
  if (scanning) {
    server.send(409, "text/plain", "BUSY");
    return;
  }

  scanning = true;
  performBeamScan(cfg.beamAngleDeg, true);
  scanning = false;
  server.send(200, "application/json", createDataJSON());
}

static void handleSweep() {
  if (scanning) {
    server.send(409, "text/plain", "BUSY");
    return;
  }

  continuousMode = false;
  scanning = true;

  const float angles[] = {-15.0f, -10.0f, -5.0f, 0.0f, 5.0f, 10.0f, 15.0f};
  const uint8_t angleCount = sizeof(angles) / sizeof(angles[0]);

  for (uint8_t a = 0; a < angleCount; ++a) {
    for (uint8_t repeat = 0; repeat < cfg.sweepRepeats; ++repeat) {
      performBeamScan(angles[a], true);
      delay(20);
    }
  }

  scanning = false;
  server.send(200, "application/json", createDataJSON());
}

static void handleContinuous() {
  if (server.hasArg("on")) continuousMode = server.arg("on").toInt() == 1;
  server.send(200, "text/plain", continuousMode ? "ON" : "OFF");
}

static void handleClear() {
  logCount = 0;
  server.send(200, "text/plain", "CLEARED");
}

static void handleCSV() {
  server.sendHeader("Content-Disposition", "attachment; filename=beamforming_results.csv");
  server.setContentLength(CONTENT_LENGTH_UNKNOWN);
  server.send(200, "text/csv", "");

  server.sendContent(
    "ScanID,Angle_deg,Known_cm,ADC_Mode,NoiseAvg,NoiseMax,Threshold,PeakADC,PeakV,Status,FirstCross_us,BeamRefDelay_us,BeamTOF_us,PeakTime_us,RawDistance_cm,CalDistance_cm\n"
  );

  char line[320];
  for (size_t i = 0; i < logCount; ++i) {
    const LogRow &r = logRows[i];

    if (r.detected) {
      snprintf(
        line, sizeof(line),
        "%lu,%.1f,%.2f,%s,%.2f,%u,%u,%u,%.4f,ECHO,%lu,%.3f,%.3f,%lu,%.3f,%.3f\n",
        static_cast<unsigned long>(r.scanId), r.angleDeg, r.knownCm,
        r.differential ? "DIFF" : "SINGLE", r.noiseAverage,
        r.noiseMaximum, r.threshold, r.peakADC, r.peakVoltage,
        static_cast<unsigned long>(r.firstCrossUs), r.beamReferenceDelayUs,
        r.beamTofUs, static_cast<unsigned long>(r.peakTimeUs),
        r.rawDistanceCm, r.calibratedDistanceCm
      );
    } else {
      snprintf(
        line, sizeof(line),
        "%lu,%.1f,%.2f,%s,%.2f,%u,%u,%u,%.4f,NO_ECHO,NA,%.3f,NA,%lu,NA,NA\n",
        static_cast<unsigned long>(r.scanId), r.angleDeg, r.knownCm,
        r.differential ? "DIFF" : "SINGLE", r.noiseAverage,
        r.noiseMaximum, r.threshold, r.peakADC, r.peakVoltage,
        r.beamReferenceDelayUs, static_cast<unsigned long>(r.peakTimeUs)
      );
    }

    server.sendContent(line);
    if ((i & 15U) == 15U) delay(0);
  }

  server.sendContent("");
}

// ============================================================
// WIFI
// ============================================================

static void startWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  Serial.print("Connecting to WiFi");
  const uint32_t startMs = millis();

  while (WiFi.status() != WL_CONNECTED && millis() - startMs < 20000) {
    delay(300);
    Serial.print(".");
  }

  Serial.println();

  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("IP address: ");
    Serial.println(WiFi.localIP());
    if (MDNS.begin("ultrasonic")) {
      Serial.println("Try: http://ultrasonic.local");
    }
  } else {
    Serial.println("WiFi failed; starting fallback AP");
    WiFi.mode(WIFI_AP);
    WiFi.softAP("Ultrasonic-Beam", "12345678");
    Serial.print("Fallback IP: ");
    Serial.println(WiFi.softAPIP());
  }
}

// ============================================================
// SETUP / LOOP
// ============================================================

void setup() {
  Serial.begin(115200);
  delay(700);

  loadSettings();
  ticksPerUs = ESP.getCpuFreqMHz();

  allTxMask = 0;
  for (uint8_t i = 0; i < N_TX; ++i) {
    pinMode(TX_PINS[i], OUTPUT);
    digitalWrite(TX_PINS[i], LOW);
    allTxMask |= (1ULL << TX_PINS[i]);
  }
  allTxOff();

  pinMode(MCP_CS, OUTPUT);
  digitalWrite(MCP_CS, HIGH);
  SPI.begin(MCP_SCK, MCP_MISO, MCP_MOSI, MCP_CS);

  startWiFi();

  server.on("/", handleRoot);
  server.on("/api/data", handleData);
  server.on("/api/set", handleSet);
  server.on("/api/scan", handleScan);
  server.on("/api/sweep", handleSweep);
  server.on("/api/continuous", handleContinuous);
  server.on("/api/clear", handleClear);
  server.on("/api/csv", handleCSV);
  server.begin();

  Serial.println();
  Serial.println("8-TX beamforming web server ready.");
  Serial.println("CSV columns:");
  Serial.println("ScanID,Angle_deg,Known_cm,ADC_Mode,NoiseAvg,NoiseMax,Threshold,PeakADC,PeakV,Status,FirstCross_us,BeamRefDelay_us,BeamTOF_us,PeakTime_us,RawDistance_cm,CalDistance_cm");
}

void loop() {
  server.handleClient();

  if (
    continuousMode && !scanning &&
    (millis() - lastContinuousScanMs >= cfg.scanIntervalMs)
  ) {
    lastContinuousScanMs = millis();
    scanning = true;
    performBeamScan(cfg.beamAngleDeg, true);
    scanning = false;
  }

  delay(1);
}
