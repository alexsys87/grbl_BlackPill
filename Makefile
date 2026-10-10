# Production firmware build for Ubuntu / WSL. Renode keeps test/gcc/Makefile.
.DEFAULT_GOAL := all

CHIP ?= F401C
CONFIG ?= Release
USB_SERIAL_CDC ?= 1
TOOLCHAIN_PREFIX ?= arm-none-eabi-
PYTHON ?= python3
EXTRA ?=

ifeq ($(CHIP),F401C)
DEVICE := STM32F401xC
STARTUP := gcc/startup_stm32f401xc.s
FLASH_SIZE := 256K
RAM_SIZE := 64K
else ifeq ($(CHIP),F401E)
DEVICE := STM32F401xE
STARTUP := gcc/startup_stm32f401xc.s
FLASH_SIZE := 512K
RAM_SIZE := 96K
else ifeq ($(CHIP),F411E)
DEVICE := STM32F411xE
STARTUP := gcc/startup_stm32f411xe.s
FLASH_SIZE := 512K
RAM_SIZE := 128K
else
$(error Unsupported CHIP '$(CHIP)'; use F401C, F401E or F411E)
endif

ifeq ($(CONFIG),Release)
OPT := -Os
else ifeq ($(CONFIG),Debug)
OPT := -Og
else
$(error Unsupported CONFIG '$(CONFIG)'; use Release or Debug)
endif

ifeq ($(USB_SERIAL_CDC),1)
PORT := usb
else ifeq ($(USB_SERIAL_CDC),0)
PORT := uart
else
$(error USB_SERIAL_CDC must be 0 or 1)
endif

CC := $(TOOLCHAIN_PREFIX)gcc
OBJCOPY := $(TOOLCHAIN_PREFIX)objcopy
SIZE := $(TOOLCHAIN_PREFIX)size
BUILD := build/gcc/$(CHIP)/$(CONFIG)/$(PORT)
TARGET := $(BUILD)/grbl
SOURCES := $(sort $(wildcard cmsis/*.c driver/*.c grbl/*.c grbl/kinematics/*.c)) gcc/syscalls.c
OBJECTS := $(addprefix $(BUILD)/,$(SOURCES:.c=.o)) $(BUILD)/gcc/startup.o
CPUFLAGS := -mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16 -mfloat-abi=hard
CPPFLAGS := -D$(DEVICE) -DUSB_SERIAL_CDC=$(USB_SERIAL_CDC) $(EXTRA) \
            -I. -Icmsis/core -Icmsis/device -Idriver -include driver/cnc3018_defaults.h
# Match IAR's unsigned plain char and preincluded CNC defaults.
CFLAGS := $(CPUFLAGS) -std=gnu11 -funsigned-char $(OPT) -g3 \
          -ffunction-sections -fdata-sections -Wall -Wno-unused-parameter \
          -Werror=implicit-function-declaration -MMD -MP
# Optional grblHAL spindle linearization uses sscanf/snprintf with floats.
# newlib-nano needs these entry points explicitly enabled (also fits F401C).
LDFLAGS := $(CPUFLAGS) $(OPT) -T gcc/stm32f4.ld \
           -Wl,--defsym=__flash_size=$(FLASH_SIZE),--defsym=__ram_size=$(RAM_SIZE) \
           -Wl,--gc-sections,--print-memory-usage,-Map=$(TARGET).map \
           --specs=nano.specs --specs=nosys.specs -Wl,-u,_printf_float,-u,_scanf_float

.PHONY: all check size all-chips clean help FORCE
all: $(TARGET).elf $(TARGET).bin $(TARGET).hex check size

# Rebuild if toolchain/options/source list change, but not on an unchanged make.
$(BUILD)/build-options.txt: FORCE
	@mkdir -p $(BUILD)
	@{ printf '%s\n' '$(CC)' '$(CPPFLAGS)' '$(CFLAGS)' '$(LDFLAGS)' '$(SOURCES)'; $(CC) --version; } > $@.tmp
	@cmp -s $@.tmp $@ && rm -f $@.tmp || mv -f $@.tmp $@

$(BUILD)/%.o: %.c $(BUILD)/build-options.txt Makefile
	@mkdir -p $(dir $@)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

$(BUILD)/gcc/startup.o: $(STARTUP) $(BUILD)/build-options.txt Makefile
	@mkdir -p $(dir $@)
	$(CC) $(CPUFLAGS) -g3 -c $< -o $@

$(TARGET).elf: $(OBJECTS) gcc/stm32f4.ld Makefile
	$(CC) $(LDFLAGS) $(OBJECTS) -lm -o $@

$(TARGET).bin: $(TARGET).elf
	$(OBJCOPY) --gap-fill 0xff -O binary $< $@

$(TARGET).hex: $(TARGET).elf
	$(OBJCOPY) -O ihex $< $@

check: $(TARGET).elf $(TARGET).bin $(TARGET).hex
	$(PYTHON) tools/check_firmware.py --chip $(CHIP) --elf $(TARGET).elf --hex $(TARGET).hex --bin $(TARGET).bin

size: $(TARGET).elf
	$(SIZE) $<

all-chips:
	@set -e; for chip in F401C F401E F411E; do \
	  $(MAKE) CHIP=$$chip CONFIG=$(CONFIG) USB_SERIAL_CDC=$(USB_SERIAL_CDC) all; \
	done

clean:
	rm -rf build/gcc

help:
	@printf '%s\n' 'make [-j] CHIP=F401C|F401E|F411E CONFIG=Release|Debug' \
	  'make all-chips          Build all three chips' \
	  'make USB_SERIAL_CDC=0   Hardware UART build (USART1)' \
	  'make clean             Remove production GCC outputs only' \
	  'Outputs: build/gcc/<CHIP>/<CONFIG>/<usb|uart>/grbl.{elf,bin,hex,map}'

-include $(OBJECTS:.o=.d)
