/* qemu encoding check: the image sits 32 MiB into vexpress-a9's RAM (0x60000000, -m 128M) so test routines can be placed up to
 * +-31.9 MiB on either side of the eboot-side helpers (the A32 BL reach; Thumb-2 is +-16 MiB) */
ENTRY(_start)
SECTIONS
{
	. = 0x62000000;
	.text : { *(.text.start) *(.text*) }
	.rodata : { *(.rodata*) }
	.data : { *(.data*) }
	. = ALIGN(4);
	__bss_start = .;
	.bss : { *(.bss*) *(COMMON) }
	. = ALIGN(4);
	__bss_end = .;
	. = ALIGN(16);
	. = . + 0x10000;
	__stack_top = .;
	/DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment) }
}
