/* devlog_pad.h: the answer function of devlog_ask_console for the Vita's buttons (debugScreen apps).
 *   #include "../devlog/devlog_unity.c"
 *   #include "../devlog/devlog_pad.h"          (needs SceCtrl_stub, which every app that reads the pad links already)
 *   if (devlog_should_ask()) devlog_ask_console(devlog_pad_answer, 30000, &res);
 * X = send now (1), CIRCLE = delete them (0), START or the timeout = not now, ask again next time (-1). Only a new
 * press counts (a button still held from the app's own menu does not answer the question). */
#ifndef SHEPHERD_DEVLOG_PAD_H
#define SHEPHERD_DEVLOG_PAD_H
#if defined(__vita__)
#include <psp2/ctrl.h>
#include <psp2/kernel/threadmgr.h>
#include <string.h>

static int devlog_pad_answer(unsigned timeout_ms)
{
	SceCtrlData pad;
	unsigned held = ~0u;   /* everything counts as held until it was seen released once */
	for (unsigned t = 0; t < timeout_ms; t += 16) {
		memset(&pad, 0, sizeof pad);
		sceCtrlPeekBufferPositive(0, &pad, 1);
		unsigned now = pad.buttons, pressed = now & ~held;
		held &= now;
		if (pressed & SCE_CTRL_CROSS) return 1;
		if (pressed & SCE_CTRL_CIRCLE) return 0;
		if (pressed & SCE_CTRL_START) return -1;
		sceKernelDelayThread(16 * 1000);
	}
	return -1;
}
#endif
#endif
