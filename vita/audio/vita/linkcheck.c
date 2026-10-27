/* Vita link check: references every public shepaudio entry point so the static library links against the real
 * SceAudio / SceAudiodec / SceKernel stubs (vita-elf-create then resolves every import NID). Not packaged as a VPK.
 * If ever run on a device it plays nothing: master volume 0, no events started. */
#include "shepaudio.h"
#include <stdio.h>

static void cb(uint32_t t, sa_handle h, void *u) { (void)t; (void)h; (void)u; }

int main(void)
{
    sa_config c; sa_config_defaults(&c);
    sa_system *s = sa_init_ex("ux0:data/shepherd/audiobake/menu/manifest.json", &c);
    if (!s) { printf("shepaudio: %s\n", sa_last_error()); return 1; }
    sa_set_master_volume(s, 0.0f);
    sa_vita_out_config oc = { 1, 512, 64, 0, 64 * 1024 };
    sa_vita_out_start(s, &oc);
    sa_vec3 z = { 0, 0, 0 }, f = { 0, 0, 1 }, u = { 0, 1, 0 };
    sa_set_listener(s, &z, &z, &f, &u);
    int e = sa_event_find(s, "event:/none");
    sa_event_info inf; sa_param_info pi; float v; int iv; void *ud; sa_stats st; sa_notification n[4];
    sa_event_get_info(s, e, &inf); sa_event_get_param(s, e, 0, &pi); sa_event_find_param(s, e, "x");
    sa_event_find_guid(s, inf.guid); sa_event_preload(s, e, 0); sa_event_unload(s, e); sa_event_count(s);
    sa_handle h = sa_event_create(s, "event:/none");
    h |= sa_event_create_guid(s, inf.guid) | sa_event_create_index(s, e);
    sa_instance_set_callback(s, h, cb, SA_CB_STOPPED); sa_instance_set_user_data(s, h, NULL); sa_instance_get_user_data(s, h, &ud);
    sa_instance_set_param(s, h, "x", 1); sa_instance_set_param_index(s, h, 0, 1); sa_instance_get_param(s, h, "x", &v);
    sa_instance_set_volume(s, h, 1); sa_instance_get_volume(s, h, &v); sa_instance_set_pitch(s, h, 1); sa_instance_get_pitch(s, h, &v);
    sa_instance_set_paused(s, h, 0); sa_instance_get_paused(s, h, &iv); sa_instance_set_3d(s, h, &z, &z);
    sa_instance_start(s, h); sa_instance_get_playback_state(s, h); sa_instance_is_valid(s, h); sa_instance_get_event(s, h);
    sa_instance_get_timeline_ms(s, h, &iv); sa_instance_stop(s, h, 1); sa_instance_release(s, h);
    sa_bus_count(s); sa_bus_find(s, "bus:/"); sa_bus_path(s, 0); sa_bus_set_volume(s, "bus:/", 1); sa_bus_get_volume(s, "bus:/", &v);
    sa_bus_set_mute(s, "bus:/", 0); sa_bus_set_paused(s, "bus:/", 0); sa_bus_stop_all(s, "bus:/", 1);
    sa_handle sn = sa_snapshot_start(s, "snapshot:/none"); sa_snapshot_stop(s, sn, 1);
    sa_set_global_param(s, "g", 0); sa_get_global_param(s, "g", &v); sa_set_poll_mask(s, 0); sa_poll(s, n, 4);
    sa_update(s); sa_get_stats(s, &st); sa_set_timing(s, 1); sa_get_mix_clock(s); sa_version();
    sa_vita_out_stop(s);
    float fb[2 * 64]; int16_t sb[2 * 64];
    sa_mix(s, fb, 64); sa_mix_s16(s, sb, 64);
    sa_file_render(s, "ux0:data/shepherd/linkcheck.wav", 0, 256, 0);
    /* Stage C: the engine bridge's entry points */
    double sv[SA_STATV_COUNT];
    sa_get_stats_v(s, sv, SA_STATV_COUNT); sa_instance_set_timeline_ms(s, h, 0);
    if (sa_file_open(s, "ux0:data/shepherd/linkcheck.wav", 1) == SA_OK) { sa_file_write(s, 0); sa_file_close(s); }
    sa_shutdown(s);
    sa_system *s2 = sa_init_opts("ux0:data/shepherd/audiobake/menu/manifest.json", "sync_io=0", NULL, NULL, NULL);
    sa_shutdown(s2);
    return 0;
}
