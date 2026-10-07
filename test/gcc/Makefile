# Cross-check build with arm-none-eabi-gcc. Not needed for IAR, it only
# verifies that the sources are clean C and builds the image for the
# Renode tests.
#   make                      default (USB CDC host port, STM32F401CC)
#   make CHIP=F401E           STM32F401CE (512 KB flash, 96 KB RAM)
#   make TEST=1               Renode test build: host port on USART1
#   make EXTRA="-DFOO"        additional defines

CHIP  ?= F401C
TEST  ?= 0
OPT   ?= -Os
EXTRA ?=
ROOT  := ../..
BUILD := build_$(CHIP)_$(TEST)

CC      := arm-none-eabi-gcc
OBJCOPY := arm-none-eabi-objcopy
SIZE    := arm-none-eabi-size

ifeq ($(CHIP),F401E)
  DEV   := STM32F401xE
  START := $(ROOT)/test/gcc/startup_stm32f401xc.s
  MEM   := -Wl,--defsym=__flash_size=512K -Wl,--defsym=__ram_size=96K
else
  DEV   := STM32F401xC
  START := $(ROOT)/test/gcc/startup_stm32f401xc.s
  MEM   := -Wl,--defsym=__flash_size=256K -Wl,--defsym=__ram_size=64K
endif

DEFS := -D$(DEV) $(EXTRA)
ifeq ($(TEST),1)
  DEFS += -DUSB_SERIAL_CDC=0
endif

INC := -I$(ROOT) -I$(ROOT)/cmsis/core -I$(ROOT)/cmsis/device -I$(ROOT)/driver
SRC := $(wildcard $(ROOT)/cmsis/*.c $(ROOT)/driver/*.c $(ROOT)/grbl/*.c $(ROOT)/grbl/kinematics/*.c)
OBJ := $(addprefix $(BUILD)/,$(notdir $(SRC:.c=.o))) $(BUILD)/startup.o

# gnu11: newlib declares strncasecmp() only with the BSD/GNU extensions.
CFLAGS := -mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16 -mfloat-abi=hard \
          -std=gnu11 $(OPT) -g -ffunction-sections -fdata-sections \
          -Wall -Wno-unused-parameter -Werror=implicit-function-declaration \
          -include $(ROOT)/driver/cnc3018_defaults.h \
          -MMD -MP $(DEFS) $(INC)

vpath %.c $(sort $(dir $(SRC)))

all: $(BUILD)/grbl.elf
	@$(SIZE) $<

$(BUILD)/%.o: %.c | $(BUILD)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD)/startup.o: $(START) | $(BUILD)
	$(CC) -mcpu=cortex-m4 -mthumb -c $< -o $@

$(BUILD)/grbl.elf: $(OBJ)
	$(CC) -mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16 -mfloat-abi=hard $(OPT) \
	  -T stm32f4.ld $(MEM) -Wl,--gc-sections -specs=nano.specs -specs=nosys.specs \
	  -Wl,-Map=$(BUILD)/grbl.map -Wl,--print-memory-usage $^ -lm -o $@
	$(OBJCOPY) -O binary $@ $(BUILD)/grbl.bin
	$(OBJCOPY) -O ihex $@ $(BUILD)/grbl.hex

$(BUILD):
	mkdir -p $@

clean:
	rm -rf build_*

.PHONY: all clean

-include $(OBJ:.o=.d)
