/* Production GCC layout; sector 1 is reserved exclusively for settings.
 * RAM/flash sizes are supplied by the root Makefile.
 * Heap (12 KiB) and stack (4 KiB) match the IAR projects.
 */
ENTRY(Reset_Handler)
_Min_Heap_Size = 0x3000;
_Min_Stack_Size = 0x1000;
MEMORY
{
  FLASH_VEC (rx) : ORIGIN = 0x08000000, LENGTH = 16K
  NVS       (r)  : ORIGIN = 0x08004000, LENGTH = 16K
  FLASH     (rx) : ORIGIN = 0x08008000, LENGTH = __flash_size - 32K
  RAM      (xrw) : ORIGIN = 0x20000000, LENGTH = __ram_size
}
_estack = ORIGIN(RAM) + LENGTH(RAM);
__StackLimit = _estack - _Min_Stack_Size;
SECTIONS
{
  .isr_vector : {
    . = ALIGN(4);
    KEEP(*(.isr_vector))
    *startup.o(.text*)
    . = ALIGN(4);
  } > FLASH_VEC
  .text : {
    *(.text .text.*)
    *(.rodata .rodata.*)
    KEEP(*(.init))
    KEEP(*(.fini))
    . = ALIGN(4);
    _etext = .;
  } > FLASH
  .ARM.extab : { *(.ARM.extab* .gnu.linkonce.armextab.*) } > FLASH
  .ARM.exidx : {
    __exidx_start = .;
    *(.ARM.exidx* .gnu.linkonce.armexidx.*)
    __exidx_end = .;
  } > FLASH
  .preinit_array : {
    PROVIDE_HIDDEN(__preinit_array_start = .);
    KEEP(*(.preinit_array*))
    PROVIDE_HIDDEN(__preinit_array_end = .);
  } > FLASH
  .init_array : {
    PROVIDE_HIDDEN(__init_array_start = .);
    KEEP(*(SORT(.init_array.*))) KEEP(*(.init_array))
    PROVIDE_HIDDEN(__init_array_end = .);
  } > FLASH
  .fini_array : {
    PROVIDE_HIDDEN(__fini_array_start = .);
    KEEP(*(SORT(.fini_array.*))) KEEP(*(.fini_array))
    PROVIDE_HIDDEN(__fini_array_end = .);
  } > FLASH
  _sidata = LOADADDR(.data);
  .data : {
    . = ALIGN(4); _sdata = .;
    *(.data .data.*) *(.ramfunc .ramfunc.*)
    . = ALIGN(4); _edata = .;
  } > RAM AT> FLASH
  .bss (NOLOAD) : {
    . = ALIGN(4); _sbss = .; __bss_start__ = .;
    *(.bss .bss.*) *(COMMON)
    . = ALIGN(4); _ebss = .; __bss_end__ = .;
  } > RAM
  .noinit (NOLOAD) : { . = ALIGN(8); *(.noinit .noinit.*) } > RAM
  .heap (NOLOAD) : {
    . = ALIGN(8); __HeapBase = .;
    PROVIDE(end = .); PROVIDE(_end = .);
    . += _Min_Heap_Size; __HeapLimit = .;
  } > RAM
  .stack __StackLimit (NOLOAD) : {
    . += _Min_Stack_Size;
  } > RAM
  .ARM.attributes 0 : { *(.ARM.attributes) }
}
ASSERT(__HeapLimit <= __StackLimit, "RAM overflow: data/BSS/heap collide with reserved stack")
ASSERT(ADDR(.isr_vector) == 0x08000000, "Vector table must start at flash base")
