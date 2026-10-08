/*
  my_machine.h - build configuration of the grblHAL driver for the
                 CNC 3018 on a WeAct Studio STM32F401 "Black Pill".

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  Only compile time options of the driver live here. Default values of the
  run time settings ($100, $110, ...) are in cnc3018_defaults.h, which the
  build pre-includes into every file (the grblHAL core reads them too).
*/

#ifndef _MY_MACHINE_H_
#define _MY_MACHINE_H_

// Board map, the only one supported by this driver.
#define BOARD_CNC3018_BLACKPILL

// Host connection:
//   1 - native USB (USB CDC virtual COM port on PA11/PA12), default.
//   0 - USART1 on PA9 (TX) / PA10 (RX), e.g. for a USB-UART adapter,
//       Bluetooth module or the Renode tests.
#ifndef USB_SERIAL_CDC
#define USB_SERIAL_CDC          1
#endif

// Serial ports in parallel with the host port, e.g. for a WiFi module
// (ESP-01 / ESP8266 with esp-link or ESP3D, ESP32 serial bridge) or a
// Bluetooth module (HC-05), 8N1:
//   UART1_ENABLE  USART1 on PA9 (TX) / PA10 (RX). Only with USB_SERIAL_CDC
//                 = 1, otherwise USART1 is the host port anyway.
//   UART2_ENABLE  USART2 on PA2 (TX) / PA3 (RX).
// Input comes from one port at a time: another port takes over when it
// sends while the machine stands still. Real time commands (?, !, ~,
// Ctrl-X) work from every port, status reports and alarms go to all.
// See driver/stream_mux.c.
#ifndef UART1_ENABLE
#define UART1_ENABLE            1
#endif
#ifndef UART2_ENABLE
#define UART2_ENABLE            1
#endif
#define UART1_BAUD_RATE         115200
#define UART2_BAUD_RATE         115200

// Settings are stored in flash sector 1 (16 KB at 0x08004000), see the
// linker files in cmsis/linker and test/gcc (FLASH_ENABLE is set by the
// core when no I2C EEPROM is configured).

// Spindle: PWM output (PA8, TIM1_CH1) + enable (PB1) + direction (PB2).
// Works for the stock 775 spindle MOSFET board and for laser modules.
// SPINDLE0_ENABLE is left at the core default (SPINDLE_PWM0).

// Coolant outputs (M7 mist, M8 flood) on PB5 / PB4.
#define COOLANT_ENABLE          (COOLANT_FLOOD|COOLANT_MIST)

// Probe input (PB15), G38.x. Probe plate to GND, internal pull-up.
#define PROBE_ENABLE            1

// Control inputs on PB6 (reset / e-stop), PB7 (feed hold), PB8 (cycle start).
// Set CONTROL_ENABLE to 0 when nothing is wired there.
//#define CONTROL_ENABLE        0

// Safety door switch on PB9. Disabled by default: the 3018 has no door.
//#define SAFETY_DOOR_ENABLE    1

// The reset input (PB6) is a plain reset button (NO to GND). Set to 1 to
// make it an e-stop input: while it is active the controller stays in
// alarm. A real e-stop should be an NC contact: then also clear its bit in
// $14 (inversion of the control inputs).
#define ESTOP_ENABLE            0

#endif // _MY_MACHINE_H_
