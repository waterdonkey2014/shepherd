/* Bare-metal layout for QEMU's vexpress-a9 (RAM at 0x60000000; run with -m 256M). Ours. */
ENTRY(_start)
MEMORY { RAM (rwx) : ORIGIN = 0x60010000, LENGTH = 0x0FFF0000 }
SECTIONS
{
  .text : { KEEP(*(.text.start)) *(.text .text.* .gnu.linkonce.t.*) *(.glue_7 .glue_7t .vfp11_veneer .v4_bx) } > RAM
  .rodata : { *(.rodata .rodata.* .gnu.linkonce.r.*) } > RAM
  .ARM.extab : { *(.ARM.extab* .gnu.linkonce.armextab.*) } > RAM
  .ARM.exidx : { __exidx_start = .; *(.ARM.exidx* .gnu.linkonce.armexidx.*) __exidx_end = .; } > RAM
  .init_array : { __init_array_start = .; KEEP(*(SORT(.init_array.*))) KEEP(*(.init_array)) __init_array_end = .; } > RAM
  .data : { *(.data .data.* .gnu.linkonce.d.*) } > RAM
  .bss (NOLOAD) : ALIGN(16384) { __bss_start = .; *(.bss .bss.* COMMON) . = ALIGN(16); __bss_end = .; } > RAM
  __heap_start = ALIGN(4096);
  __stack_top = ORIGIN(RAM) + LENGTH(RAM);
  __heap_end = __stack_top - 0x100000;
  /DISCARD/ : { *(.note.*) *(.comment) }
}
