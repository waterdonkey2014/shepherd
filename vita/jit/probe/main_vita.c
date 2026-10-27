/* Shepherd JIT probe, device entry (vita/jit/probe/README.md). Runs the probe harness with auto_confirm OFF: on the console nothing is
 * allocated or generated until the owner confirms (plat_confirm -> X). BUILT in kit #7, NOT YET RUN -- the first exec-memory step waits
 * for the owner's go (the hard rule: no JIT code run on a device without the owner's confirmation). */
#include "jitp.h"
int main (void) { return jitp_main (0); }
