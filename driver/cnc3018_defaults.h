/*
  cnc3018_defaults.h - default settings for a stock CNC 3018 (3018 PRO).

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  This file is pre-included into every source file of the build
  (IAR: C/C++ Compiler -> Preprocessor -> Preinclude file,
   GCC: -include cnc3018_defaults.h), so the grblHAL core picks the values
  up without any change to grbl/config.h.

  These are only the values written by "$RST=$" / on the first start with
  empty settings. Change a value at run time with "$100=400" etc., the
  setting is stored in flash. After changing this file reflash and send
  "$RST=$" to load the new defaults.

  Only #define lines here (and one C prototype, guarded): the file is also
  seen by the CMSIS sources.
*/

#ifndef _CNC3018_DEFAULTS_H_
#define _CNC3018_DEFAULTS_H_

#define BUILD_INFO "CNC3018 BlackPill"

// 3 axes, no ganged motors.
#define N_AXIS 3

// Mechanics of a stock 3018: 1.8 deg motors, 1/16 microstepping,
// T8 lead screws. $100..$102
#define DEFAULT_X_STEPS_PER_MM 800.0f
#define DEFAULT_Y_STEPS_PER_MM 800.0f
#define DEFAULT_Z_STEPS_PER_MM 800.0f

// Maximum rates, mm/min. $110..$112
#define DEFAULT_X_MAX_RATE 1000.0f
#define DEFAULT_Y_MAX_RATE 1000.0f
#define DEFAULT_Z_MAX_RATE 600.0f

// Accelerations, mm/s^2. $120..$122
#define DEFAULT_X_ACCELERATION 50.0f
#define DEFAULT_Y_ACCELERATION 50.0f
#define DEFAULT_Z_ACCELERATION 50.0f

// Working area 300 x 180 x 45 mm. $130..$132
#define DEFAULT_X_MAX_TRAVEL 300.0f
#define DEFAULT_Y_MAX_TRAVEL 180.0f
#define DEFAULT_Z_MAX_TRAVEL 45.0f

// Stepper drivers (A4988 / DRV8825 / TMC2208 in legacy mode). $0, $1, $4
#define DEFAULT_STEP_PULSE_MICROSECONDS 4.0f
#define DEFAULT_STEPPER_IDLE_LOCK_TIME 25

// Spindle: 775 motor via MOSFET, 0..10000 rpm on S. $30, $31, $33
#define DEFAULT_SPINDLE_RPM_MAX 10000.0f
#define DEFAULT_SPINDLE_RPM_MIN 0.0f
#define DEFAULT_SPINDLE_PWM_FREQ 1000

// The stock 3018 has no limit switches: homing, hard and soft limits off.
// Enable with $22=1, $21=1, $20=1 after fitting switches (NO to GND).
#define DEFAULT_HOMING_ENABLE 0
#define DEFAULT_HARD_LIMIT_ENABLE 0
#define DEFAULT_SOFT_LIMIT_ENABLE 0
#define DEFAULT_HOMING_DIR_MASK 0
#define DEFAULT_HOMING_FEED_RATE 100.0f
#define DEFAULT_HOMING_SEEK_RATE 800.0f
#define DEFAULT_HOMING_PULLOFF 2.0f

// Switch logic. The inputs have internal pull-ups and the switches go to
// GND. grblHAL expects normally closed (NC) switches by default, the 3018
// is usually fitted with normally open (NO) ones, so the logic is
// inverted: a low pin is "triggered".
//   $5  limit switches NO (= 7). Use $5=0 for NC switches.
//   $14 control buttons (reset, feed hold, cycle start, door) NO.
//   $6  probe: touching the plate pulls the pin low.
// With nothing connected the pins stay high: nothing is triggered.
#define DEFAULT_LIMIT_SIGNALS_INVERT_MASK 7
#define DEFAULT_CONTROL_SIGNALS_INVERT_MASK -1
#define DEFAULT_PROBE_SIGNAL_INVERT 1

// IAR DLIB has no strncasecmp() (POSIX), the core uses it. compat.c
// provides it, this prototype makes it visible everywhere.
#if defined(__ICCARM__) && !defined(__ASSEMBLER__) && !defined(__IAR_SYSTEMS_ASM__)
#include <stddef.h>
int strncasecmp (const char *s1, const char *s2, size_t n);
#endif

#endif // _CNC3018_DEFAULTS_H_
