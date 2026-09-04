# Requirements

## Hardware

- ESP32-WROOM development board
- Custom 8-channel ultrasonic transmitter PCB
- Eight TCT40-16T nominal 40-kHz transmitters
- One TCT40-16R receiver
- MCP3008 10-bit ADC
- TL072 analog front end
- 1N5819 Schottky envelope detector and RC network
- Eight IRLZ44N low-side TX switches
- Regulated 12-V analog/TX supply and 3.3-V logic supply
- Oscilloscope and measuring tape for calibration
- USB cable and a 2.4-GHz Wi-Fi network for the dashboard

Do not power the 12-V TX/RX rail from the ESP32 3.3-V pin. Confirm grounds,
polarity, current capacity, and the receiver input range before connecting the
MCP3008.

## Software

- Arduino IDE 2.x or PlatformIO
- Espressif ESP32 board support
- Python 3.10 or newer for optional offline analysis
- Git for cloning the repository

The firmware uses standard ESP32/Arduino libraries included with the board
package: `Arduino.h`, `WiFi.h`, `WebServer.h`, `ESPmDNS.h`, `SPI.h`, and
`Preferences.h`. No HC-SR04 or NewPing dependency is required by the current
custom-board firmware.

For the optional legacy serial plotter:

```text
py -m pip install pyserial matplotlib numpy
```

## Firmware setup

1. Clone the repository:

   ```text
   git clone https://github.com/saadw50/ultrasound-phased-array-imaging.git
   cd ultrasound-phased-array-imaging
   ```

2. Open exactly one `.ino` sketch in Arduino IDE.
3. Set `WIFI_SSID` and `WIFI_PASSWORD` locally before flashing. Never commit
   real credentials.
4. Select the ESP32 board and the correct serial port.
5. Upload and open Serial Monitor at 115200 baud.
6. Use the printed IP address or the mDNS address shown by the firmware.

## Current acquisition scope

The current firmware performs sequential TX scanning with one receiver. It is
not simultaneous phased-array transmission. The raw-waveform research paper
uses separate offline captures and documents the timing calibration issue.
