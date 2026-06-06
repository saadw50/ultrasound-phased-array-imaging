#include <Arduino.h>
#include <SPI.h>
#include <math.h>

// ==========================================
// 1. HARDWARE PIN DEFINITIONS
// ==========================================
// The 8 TX Pins mapped to your ESP32-WROOM-32D physical layout
const int TX_PINS[8] = {13, 12, 14, 27, 26, 25, 33, 32}; 

// The RX Pins (Hardware VSPI for maximum speed)
const int CS_PIN = 5; 
// Note: CLK is physically D18, MISO is D19, MOSI is D23 by default on ESP32 VSPI.

// ==========================================
// 2. PHASED ARRAY PHYSICS CONSTANTS
// ==========================================
const int NUM_TX = 8;
const float SPEED_OF_SOUND = 343.0; // Meters per second

// IMPORTANT: Measure the exact distance from the center of Transducer 1 
// to the center of Transducer 2 in meters. (16mm = 0.016m)
const float TRANSDUCER_SPACING = 0.016; 

// FreeRTOS Task Handle
TaskHandle_t BeamformingTaskHandle;

// ==========================================
// 3. MCP3008 HIGH-SPEED SPI FUNCTION
// ==========================================
int readMCP3008() {
  // The MCP3008 maxes out around 3.6MHz at 5V, so we push the ESP32 SPI clock fast
  SPI.beginTransaction(SPISettings(3000000, MSBFIRST, SPI_MODE0));
  
  digitalWrite(CS_PIN, LOW);
  
  // Send Start Bit (0x01), then Single-Ended CH0 Command (0x80), then Dummy Byte (0x00)
  SPI.transfer(0x01); 
  byte msb = SPI.transfer(0x80); 
  byte lsb = SPI.transfer(0x00);
  
  digitalWrite(CS_PIN, HIGH);
  SPI.endTransaction();
  
  // Combine the 10 bits of data
  int adcValue = ((msb & 0x03) << 8) | lsb;
  return adcValue;
}

// ==========================================
// 4. BEAMFORMING & FIRING SEQUENCE
// ==========================================
void fireSteeredBeam(float angleDegrees) {
  // Convert angle to radians for the C++ math library
  float angleRadians = angleDegrees * (PI / 180.0);
  
  // Calculate the time delay between adjacent elements (in seconds)
  float timeDelaySec = (TRANSDUCER_SPACING * sin(angleRadians)) / SPEED_OF_SOUND;
  
  // Convert to positive microseconds for the delay loop
  int delayUS = abs(timeDelaySec * 1000000.0);

  // Fire the array (Staggered to create the angled wavefront)
  if (angleDegrees >= 0) {
    // Steer Right: Fire Left-to-Right
    for (int i = 0; i < NUM_TX; i++) {
      digitalWrite(TX_PINS[i], HIGH);
      delayMicroseconds(delayUS);
    }
  } else {
    // Steer Left: Fire Right-to-Left
    for (int i = NUM_TX - 1; i >= 0; i--) {
      digitalWrite(TX_PINS[i], HIGH);
      delayMicroseconds(delayUS);
    }
  }

  // Hold the pulse HIGH for half of a 40kHz wavelength (~12.5 us) to push maximum energy
  delayMicroseconds(12);

  // Snap all MOSFETs closed simultaneously (Flyback diodes catch the spike)
  for (int i = 0; i < NUM_TX; i++) {
    digitalWrite(TX_PINS[i], LOW);
  }
}

// ==========================================
// 5. FREERTOS CORE 1 TASK (The Heavy Lifter)
// ==========================================
void beamformingTask(void * parameter) {
  for(;;) {
    // Sweep the beam from -30 to +30 degrees in 5-degree steps
    for (int currentAngle = -30; currentAngle <= 30; currentAngle += 5) {
      
      // 1. Fire the acoustic pulse
      fireSteeredBeam(currentAngle);
      
      // 2. Immediately listen for the echo (Sample the ADC rapidly)
      // For V1.0, we just take 100 fast samples to look for the peak echo
      int maxEcho = 0;
      for(int sample = 0; sample < 100; sample++) {
        int currentReading = readMCP3008();
        if(currentReading > maxEcho) {
          maxEcho = currentReading;
        }
        delayMicroseconds(50); // Wait briefly between samples
      }

      // 3. Print the data (This technically runs on Core 1 here, but can be moved to Core 0 later)
      Serial.print("Angle:");
      Serial.print(currentAngle);
      Serial.print(" , Peak_Echo:");
      Serial.println(maxEcho);
      
      // 4. Wait 50ms before the next ping to let all room echoes die out
      vTaskDelay(50 / portTICK_PERIOD_MS); 
    }
  }
}

// ==========================================
// 6. SETUP (Core 0)
// ==========================================
void setup() {
  Serial.begin(115200);
  
  // Initialize VSPI
  SPI.begin();
  pinMode(CS_PIN, OUTPUT);
  digitalWrite(CS_PIN, HIGH);

  // Initialize MOSFET Gate Pins
  for (int i = 0; i < NUM_TX; i++) {
    pinMode(TX_PINS[i], OUTPUT);
    digitalWrite(TX_PINS[i], LOW);
  }

  // Pin the highly sensitive beamforming task to Core 1
  xTaskCreatePinnedToCore(
    beamformingTask,        // Function to implement the task
    "Beamforming_Task",     // Name of the task
    10000,                  // Stack size in words
    NULL,                   // Task input parameter
    1,                      // Priority of the task (1 is high)
    &BeamformingTaskHandle, // Task handle
    1                       // Core where the task should run (Core 1)
  );
}

// Empty loop. FreeRTOS is handling everything in the background!
void loop() {
  vTaskDelete(NULL); 
}
