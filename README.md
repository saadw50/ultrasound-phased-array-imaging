## 🛠️ Hardware Assembly & Testing Guide (V1.0)

This guide covers the assembly and initial bring-up of the V1.0 Phased Array PCB. The board uses a split "Loud/Quiet" layout to isolate the high-current 12V TX array from the highly sensitive analog RX envelope detector.

### 📦 Bill of Materials (BOM)
* **Microcontroller:** ESP32 (NodeMCU or similar)
* **ADC:** 1x MCP3008 (10-bit SPI)
* **Op-Amp:** 1x TL072 (Dual JFET LNA)
* **MOSFETs:** 8x IRLZ44N (Logic-Level)
* **Diodes:** 9x 1N5819 Schottky Diodes (8 for TX flybacks, 1 for RX envelope)
* **Transducers:** 8x TCT40-16T (Transmit), 1x TCT40-16R (Receive)
* **Passives:** 
  * 100nF Ceramic Decoupling Capacitors (for IC power pins)
  * 100Ω Gate Resistors, 10kΩ Pull-downs
  * RC Filter: 47Ω Resistor + 100µF Electrolytic Capacitor
* **Hardware:** DIP IC Sockets (8-pin & 16-pin), 2.54mm Headers, Foam tape.

---

### 🔧 Phase 1: PCB Soldering
To ensure the board lays flat while working, solder components in order of height (lowest to highest):

1. **Passives & Diodes:** Solder all 100Ω and 10kΩ resistors. Solder the nine 1N5819 Schottky diodes, ensuring the cathode (silver stripe) matches the PCB silkscreen.
2. **IC Sockets:** **Do not solder the TL072 or MCP3008 directly to the board.** Solder the DIP sockets first to protect the silicon from heat damage.
3. **Capacitors:** Solder the 100nF ceramic decoupling capacitors close to the IC pads, followed by the larger 100µF electrolytic filter capacitor (note the ground stripe polarity).
4. **Power Drivers & Headers:** Solder the IRLZ44N MOSFETs, screw terminals, and ESP32 connection headers.
5. **Transducers:** Solder the 8 TX transducers perfectly flush with the PCB for accurate beamforming math. 
   > ⚠️ **Critical Mechanical Step:** Do NOT solder the RX transducer flush to the board. Mount it on a small square of thick double-sided foam tape before soldering. This physically isolates the microphone from the fiberglass, preventing acoustic "ringdown" from the TX array and dropping the blind zone to near-zero.

---

### ⚡ Phase 2: The "Smoke Test" (No ICs Installed)
Before inserting the TL072 or MCP3008 into their sockets, verify the power rails:
1. Connect the 12V power supply to the screw terminals.
2. Probe **Pin 8** of the empty TL072 socket; it should read exactly 12V.
3. Probe the virtual ground junction (between BIAS4 and BIAS5); it should read exactly 6V.
4. If voltages are stable and nothing is heating up, disconnect power and safely insert the TL072 and MCP3008 into their sockets.

---

### 🔌 Phase 3: Star Ground Wiring Topology
Because this system switches high inductive currents (TX) next to sensitive analog signals (RX), grounding is critical to prevent frying the ESP32.

1. **TX Array:** Connect the "Dirty" 12V supply to the TX side.
2. **RX Board:** Connect the ESP32 `3V3` to power the ADC logic.
3. **The Star Ground:** Run one ground wire from the TX array header to the ESP32 `GND`. Run a *separate* ground wire from the RX header to the *exact same* ESP32 `GND` pin. Never daisy-chain the grounds.

---

### 📡 Phase 4: Initial Bring-Up
1. **TX Verification:** Connect the 8 ESP32 GPIOs to the TX array. Upload the basic sweep code. You should feel a slight acoustic pressure/breeze in front of the array, confirming the 40kHz square waves are firing.
2. **RX Verification:** Connect the 4 SPI lines (`CS`, `CLK`, `MOSI`, `MISO`) to the ESP32. Snap your fingers near the isolated RX transducer while monitoring the Serial Plotter. You should see sharp, clean voltage spikes on the MCP3008 output.
