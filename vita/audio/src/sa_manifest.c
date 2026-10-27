/* Manifest (SHARED SPEC v1) -> compact tables. */
#include "sa_internal.h"

uint32_t sa_hash_ci(const char *s)
{
    uint32_t h = 2166136261u;
    for (; *s; s++) { unsigned char c = (unsigned char)*s; if (c >= 'A' && c <= 'Z') c = (unsigned char)(c + 32); h = (h ^ c) * 16777619u; }
    return h ? h : 1;
}
static uint32_t hash_guid(const uint8_t *g)
{
    uint32_t h = 2166136261u;
    for (int i = 0; i < 16; i++) h = (h ^ g[i]) * 16777619u;
    return h ? h : 1;
}
int sa_strieq(const char *a, const char *b)
{
    for (;; a++, b++) {
        unsigned char x = (unsigned char)*a, y = (unsigned char)*b;
        if (x >= 'A' && x <= 'Z') x = (unsigned char)(x + 32);
        if (y >= 'A' && y <= 'Z') y = (unsigned char)(y + 32);
        if (x != y) return 0;
        if (!x) return 1;
    }
}
static int hexv(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }
int sa_parse_guid(const char *s, uint8_t out[16])
{
    uint8_t raw[16]; int n = 0;
    if (!s) return -1;
    if (*s == '{') s++;
    for (; *s && *s != '}' && n < 32; s++) {
        if (*s == '-') continue;
        int v = hexv(*s); if (v < 0) return -1;
        if (n & 1) raw[n >> 1] = (uint8_t)(raw[n >> 1] | v); else raw[n >> 1] = (uint8_t)(v << 4);
        n++;
    }
    if (n != 32) return -1;
    /* textual Data1/2/3 are big-endian numbers; FMOD.GUID memory is little-endian for those fields */
    out[0] = raw[3]; out[1] = raw[2]; out[2] = raw[1]; out[3] = raw[0];
    out[4] = raw[5]; out[5] = raw[4];
    out[6] = raw[7]; out[7] = raw[6];
    memcpy(out + 8, raw + 8, 8);
    return 0;
}

/* ---- string pool (fixed capacity, computed up front, never moves) ---- */
static const char *pool_str(sa_system *sys, const char *a, const char *b)
{
    size_t la = a ? strlen(a) : 0, lb = b ? strlen(b) : 0;
    if (sys->strpool_len + la + lb + 1 > sys->strpool_cap) return NULL;
    char *d = sys->strpool + sys->strpool_len;
    if (la) memcpy(d, a, la);
    if (lb) memcpy(d + la, b, lb);
    d[la + lb] = 0;
    sys->strpool_len += la + lb + 1;
    return d;
}

static uint32_t pow2_at_least(uint32_t n) { uint32_t p = 16; while (p < n) p <<= 1; return p; }
static int depth_of(const char *p) { int d = 1; for (; *p; p++) if (*p == '/') d++; return d; }

int sa_find_bus(sa_system *sys, const char *path)
{
    if (!path || !sys->h_bus) return SA_ERR_NOT_FOUND;
    uint32_t m = sys->h_bus_mask, i = sa_hash_ci(path) & m;
    for (;;) {
        int32_t v = sys->h_bus[i];
        if (!v) return SA_ERR_NOT_FOUND;
        if (sa_strieq(sys->buses[v - 1].path, path)) return v - 1;
        i = (i + 1) & m;
    }
}
static int bus_insert_hash(sa_system *sys, int idx)
{
    uint32_t m = sys->h_bus_mask, i = sa_hash_ci(sys->buses[idx].path) & m;
    while (sys->h_bus[i]) i = (i + 1) & m;
    sys->h_bus[i] = idx + 1;
    return 0;
}
/* bus by path, created (with its parents) if missing; capacity was reserved by the counting pass */
static int bus_get(sa_system *sys, const char *path)
{
    int b = sa_find_bus(sys, path);
    if (b >= 0) return b;
    if (sys->nbuses >= sys->cap_buses) return 0;
    char tmp[512];
    size_t n = strlen(path);
    if (n >= sizeof tmp) return 0;
    memcpy(tmp, path, n + 1);
    while (n > 5 && tmp[n - 1] == '/') tmp[--n] = 0;   /* "bus:/A/" -> "bus:/A" */
    if (n != strlen(path)) { b = sa_find_bus(sys, tmp); if (b >= 0) return b; }
    int parent = 0;
    char *slash = strrchr(tmp, '/');
    if (slash && slash - tmp > 4) {           /* "bus:/A/B" -> parent "bus:/A" ; "bus:/A" -> parent "bus:/" */
        char pp[512]; size_t pl = (size_t)(slash - tmp);
        memcpy(pp, tmp, pl); pp[pl] = 0;
        if (pl == 4) { pp[4] = '/'; pp[5] = 0; }
        parent = bus_get(sys, pp);
    }
    b = sys->nbuses++;
    sa_bus *bus = &sys->buses[b];
    memset(bus, 0, sizeof *bus);
    bus->path = pool_str(sys, tmp, NULL);
    if (!bus->path) { sys->nbuses--; return 0; }
    bus->parent = b == 0 ? -1 : parent;
    bus->volume = 1; bus->def_volume = 1; bus->eff_gain = 1;
    bus_insert_hash(sys, b);
    return b;
}

int sa_global_index(sa_system *sys, const char *name, int create)
{
    for (int i = 0; i < sys->nglobals; i++) if (sa_strieq(sys->gnames[i], name)) return i;
    if (!create) return SA_ERR_NOT_FOUND;
    char **nn = (char **)realloc(sys->gnames, sizeof(char *) * (size_t)(sys->nglobals + 1));
    if (!nn) return SA_ERR_NO_MEMORY;
    sys->gnames = nn;
    float *nv = (float *)realloc(sys->gvals, sizeof(float) * (size_t)(sys->nglobals + 1));
    if (!nv) return SA_ERR_NO_MEMORY;
    sys->gvals = nv;
    size_t l = strlen(name);
    sys->gnames[sys->nglobals] = (char *)malloc(l + 1);
    if (!sys->gnames[sys->nglobals]) return SA_ERR_NO_MEMORY;
    memcpy(sys->gnames[sys->nglobals], name, l + 1);
    sys->gvals[sys->nglobals] = 0;
    return sys->nglobals++;
}

static int codec_of(const char *s)
{
    if (!s) return 0;
    if (sa_strieq(s, "pcm16") || sa_strieq(s, "pcm")) return SA_CODEC_PCM16;
    if (sa_strieq(s, "ima-adpcm") || sa_strieq(s, "adpcm") || sa_strieq(s, "ima4") || sa_strieq(s, "ima")) return SA_CODEC_ADPCM;
    if (sa_strieq(s, "aac-adts") || sa_strieq(s, "aac") || sa_strieq(s, "adts")) return SA_CODEC_AAC;
    return 0;
}

static float to_db(float gain)
{
    if (!(gain > 0)) return -80.f;
    float db = 20.f * log10f(gain);
    return db < -80.f ? -80.f : (db > 24.f ? 24.f : db);
}

static void event_hash_insert(sa_system *sys, int e)
{
    uint32_t m = sys->h_event_mask;
    uint32_t i = sa_hash_ci(sys->events[e].path) & m;
    while (sys->h_event_path[i]) i = (i + 1) & m;
    sys->h_event_path[i] = e + 1;
    int zero = 1; for (int k = 0; k < 16; k++) if (sys->events[e].guid[k]) zero = 0;
    if (!zero) {
        i = hash_guid(sys->events[e].guid) & m;
        while (sys->h_event_guid[i]) i = (i + 1) & m;
        sys->h_event_guid[i] = e + 1;
    }
}
int sa_find_event_guid(sa_system *sys, const uint8_t g[16])
{
    uint32_t m = sys->h_event_mask, i = hash_guid(g) & m;
    for (;;) {
        int32_t v = sys->h_event_guid[i];
        if (!v) return SA_ERR_NOT_FOUND;
        if (!memcmp(sys->events[v - 1].guid, g, 16)) return v - 1;
        i = (i + 1) & m;
    }
}
int sa_find_event(sa_system *sys, const char *p)
{
    if (!p || !*p) return SA_ERR_NOT_FOUND;
    if (p[0] == '{') { uint8_t g[16]; if (sa_parse_guid(p, g)) return SA_ERR_NOT_FOUND; return sa_find_event_guid(sys, g); }
    uint32_t m = sys->h_event_mask, i = sa_hash_ci(p) & m;
    for (;;) {
        int32_t v = sys->h_event_path[i];
        if (!v) return SA_ERR_NOT_FOUND;
        if (sa_strieq(sys->events[v - 1].path, p)) return v - 1;
        i = (i + 1) & m;
    }
}

static int is_abs(const char *f) { return f[0] == '/' || strchr(f, ':') != NULL; }

/* params: NAN = unspecified */
static int same_pv(const float *a, const float *b, int n)
{
    if (!a || !b) return a == b;
    for (int i = 0; i < n; i++) {
        int na = a[i] != a[i], nb = b[i] != b[i];
        if (na != nb) return 0;
        if (!na && a[i] != b[i]) return 0;
    }
    return 1;
}

/* stop(fadeout) release (bake "releaseMs"/"releaseStopMs"/"releaseCurve"): (ms, gain) points -> (output frames, gain),
 * times strictly rising, ending at gain 0 */
static void parse_release(sa_system *sys, sa_event *ev, const sa_json *e, float rate)
{
    const sa_json *rc = sa_json_get(e, "releaseCurve");
    ev->first_relpt = sys->nrelpts; ev->nrelpt = 0; ev->release_stop_frames = 0;
    if (!rc || rc->type != SA_J_ARR || rc->count < 2) return;
    float *d = sys->relpts + 2 * (size_t)sys->nrelpts;
    int n = 0; float last_t = -1, last_g = 1;
    for (const sa_json *q = rc->child; q; q = q->next) {
        float t = (float)(sa_json_num(sa_json_get(q, "ms"), -1) * 0.001 * rate), g = (float)sa_json_num(sa_json_get(q, "gain"), 0);
        if (!(t >= 0) || !(g >= 0)) continue;
        if (g > 1) g = 1;
        if (n == 0 && t > 0) { d[0] = 0; d[1] = 1; n = 1; last_t = 0; }
        if (t <= last_t) { if (n) d[2 * (n - 1) + 1] = g; continue; }
        d[2 * n] = t; d[2 * n + 1] = g; n++; last_t = t; last_g = g;
    }
    if (n >= 1 && last_g > 0) { d[2 * n] = last_t + 1; d[2 * n + 1] = 0; n++; }
    if (n < 2) return;
    ev->nrelpt = n; sys->nrelpts += n;
    double stop_ms = sa_json_num(sa_json_get(e, "releaseStopMs"), sa_json_num(sa_json_get(e, "releaseMs"), d[2 * (n - 1)] * 1000.0 / rate));
    ev->release_stop_frames = (uint32_t)(stop_ms > 0 ? stop_ms * 0.001 * rate : 0);
}

/* measured 3D model (bake "rolloff" + "pan"); identical models share one entry */
static void parse_spatial(sa_system *sys, sa_event *ev, const sa_json *e)
{
    const sa_json *ro = sa_json_get(e, "rolloff"), *pn = sa_json_get(e, "pan");
    if (!(ro && ro->type == SA_J_ARR) && !(pn && pn->type == SA_J_OBJ)) return;
    sa_spatial sp; memset(&sp, 0, sizeof sp);
    if (ro && ro->type == SA_J_ARR) for (const sa_json *q = ro->child; q && sp.nroll < SA_SP_ROLL; q = q->next) {
        float d = (float)sa_json_num(sa_json_get(q, "d"), -1), g = (float)sa_json_num(sa_json_get(q, "gain"), -1);
        if (!(d >= 0) || !(g >= 0)) continue;
        if (sp.nroll && d <= sp.roll_d[sp.nroll - 1]) continue;
        sp.roll_d[sp.nroll] = d; sp.roll_g[sp.nroll] = g; sp.nroll++;
    }
    sp.has_roll = sp.nroll > 0;
    if (pn && pn->type == SA_J_OBJ) {
        sp.pan_dist = (float)sa_json_num(sa_json_get(pn, "distance"), 0);
        const sa_json *az = sa_json_get(pn, "az"), *nr = sa_json_get(pn, "near");
        if (az && az->type == SA_J_ARR) for (const sa_json *q = az->child; q && sp.naz < SA_SP_AZ; q = q->next) {
            float a = (float)sa_json_num(sa_json_get(q, "az"), -1), f = (float)sa_json_num(sa_json_get(q, "far"), -1);
            if (!(a > 0 && a <= 90) || !(f >= 0 && f <= 1)) continue;
            if (sp.naz && a <= sp.az[sp.naz - 1]) continue;
            sp.az[sp.naz] = a; sp.far[sp.naz] = f; sp.naz++;
        }
        if (nr && nr->type == SA_J_ARR) for (const sa_json *q = nr->child; q && sp.nnear < SA_SP_NEAR; q = q->next) {
            float d = (float)sa_json_num(sa_json_get(q, "d"), -1), b = (float)sa_json_num(sa_json_get(q, "blend"), -1);
            if (!(d > 0) || !(b >= 0 && b <= 1) || (sp.pan_dist > 0 && d >= sp.pan_dist)) continue;
            if (sp.nnear && d <= sp.near_d[sp.nnear - 1]) continue;
            sp.near_d[sp.nnear] = d; sp.near_b[sp.nnear] = b; sp.nnear++;
        }
        if (!(sp.pan_dist > 0)) { sp.naz = 0; sp.nnear = 0; }
    }
    for (int k = 0; k < sys->nspatials; k++) if (!memcmp(&sys->spatials[k], &sp, sizeof sp)) { ev->spatial = k; return; }
    sys->spatials[sys->nspatials] = sp;
    ev->spatial = sys->nspatials++;
}

int sa_manifest_load(sa_system *sys, const char *path, char *err, size_t errlen)
{
    size_t len = 0;
    char *text = sa_read_file(path, &len);
    if (!text) { snprintf(err, errlen, "cannot read manifest '%s'", path); return SA_ERR_FILE; }
    sa_jarena arena = { 0 };
    sa_json *root = sa_json_parse(text, len, &arena, err, errlen);
    if (!root) { free(text); sa_json_free(&arena); return SA_ERR_FORMAT; }
    int rc = SA_OK;
    int strict = (sys->cfg.flags & SA_FLAG_STRICT_MANIFEST) != 0;
    int version = (int)sa_json_num(sa_json_get(root, "version"), 0);
    if (version != 1) { snprintf(err, errlen, "manifest version %d (need 1)", version); rc = SA_ERR_FORMAT; goto done; }
    int set_rate = (int)sa_json_num(sa_json_get(root, "sampleRate"), 48000);
    const sa_json *jb = sa_json_get(root, "buses"), *je = sa_json_get(root, "events"), *js = sa_json_get(root, "snapshots");
    if (je && je->type != SA_J_ARR) { snprintf(err, errlen, "manifest: events is not an array"); rc = SA_ERR_FORMAT; goto done; }

    /* ---- base dir ---- */
    {
        const char *sl = strrchr(path, '/');
        size_t bl = sl ? (size_t)(sl - path + 1) : 0;
        sys->base_dir = (char *)malloc(bl + 1);
        if (!sys->base_dir) { rc = SA_ERR_NO_MEMORY; goto done; }
        memcpy(sys->base_dir, path, bl); sys->base_dir[bl] = 0;
    }
    size_t blen = strlen(sys->base_dir);

    /* ---- counting pass ---- */
    int nev = 0, ntk = 0, npar = 0, nbv = 0, nbus = 1, maxp = 0, nrel = 0, nsp = 0;
    size_t strb = 64;
    if (jb && jb->type == SA_J_ARR) for (const sa_json *b = jb->child; b; b = b->next) {
        const char *p = sa_json_str(sa_json_get(b, "path"), NULL);
        if (p) { int d = depth_of(p); nbus += d; strb += (size_t)d * (strlen(p) + 2); }
    }
    if (je) for (const sa_json *e = je->child; e; e = e->next) {
        const char *p = sa_json_str(sa_json_get(e, "path"), NULL);
        if (!p) continue;
        nev++; strb += strlen(p) + 1;
        const char *bp = sa_json_str(sa_json_get(e, "bus"), NULL);
        if (bp) { int d = depth_of(bp); strb += (size_t)d * (strlen(bp) + 2); nbus += d; }
        const sa_json *ps = sa_json_get(e, "params"), *ts = sa_json_get(e, "takes");
        int np = 0;
        if (ps && ps->type == SA_J_ARR) for (const sa_json *q = ps->child; q; q = q->next) {
            const char *n = sa_json_str(sa_json_get(q, "name"), NULL);
            if (n) { np++; strb += strlen(n) + 1; }
        }
        npar += np; if (np > maxp) maxp = np;
        if (ts && ts->type == SA_J_ARR) for (const sa_json *t = ts->child; t; t = t->next) {
            const char *f = sa_json_str(sa_json_get(t, "file"), NULL);
            if (f) { ntk++; strb += strlen(f) + blen + 1; }
        }
        const sa_json *rcv = sa_json_get(e, "releaseCurve");
        if (rcv && rcv->type == SA_J_ARR) nrel += rcv->count + 2;
        if (sa_json_get(e, "rolloff") || sa_json_get(e, "pan")) nsp++;
    }
    int ncurve = 0;
    if (js && js->type == SA_J_ARR) for (const sa_json *s = js->child; s; s = s->next) {
        const char *p = sa_json_str(sa_json_get(s, "path"), NULL);
        if (!p) continue;
        nev++; strb += strlen(p) + 1;
        const sa_json *bv = sa_json_get(s, "busVolumes");
        if (bv && bv->type == SA_J_OBJ) for (const sa_json *q = bv->child; q; q = q->next) {
            int d = depth_of(q->key); nbv++; strb += (size_t)d * (strlen(q->key) + 2); nbus += d;
        }
        const sa_json *ps = sa_json_get(s, "params");
        int np = 0;
        if (ps && ps->type == SA_J_ARR) for (const sa_json *q = ps->child; q; q = q->next) {
            const char *n = sa_json_str(sa_json_get(q, "name"), NULL);
            if (n) { np++; strb += strlen(n) + 1; }
        }
        npar += np; if (np > maxp) maxp = np;
        const sa_json *cv = sa_json_get(s, "curve");
        if (cv && cv->type == SA_J_ARR) for (const sa_json *c = cv->child; c; c = c->next) {
            const sa_json *pv = sa_json_get(c, "paramValues");
            int npv = pv && pv->type == SA_J_OBJ ? pv->count : 0;
            ncurve += npv ? npv : 1;
            const sa_json *cb = sa_json_get(c, "busVolumes");
            if (cb && cb->type == SA_J_OBJ) for (const sa_json *q = cb->child; q; q = q->next) {
                int d = depth_of(q->key); nbv += npv ? npv : 1; strb += (size_t)d * (strlen(q->key) + 2); nbus += d;
            }
        }
    }
    sys->strpool_cap = strb;
    sys->strpool = (char *)malloc(strb);
    sys->events = (sa_event *)calloc((size_t)(nev ? nev : 1), sizeof(sa_event));
    sys->takes = (sa_take *)calloc((size_t)(ntk ? ntk : 1), sizeof(sa_take));
    sys->take_order = (int32_t *)calloc((size_t)(ntk ? ntk : 1), sizeof(int32_t));
    sys->groups = (sa_group *)calloc((size_t)(ntk ? ntk : 1) + (size_t)nev, sizeof(sa_group));
    sys->params = (sa_param_def *)calloc((size_t)(npar ? npar : 1), sizeof(sa_param_def));
    sys->pvals = (float *)malloc(sizeof(float) * (size_t)(ntk * (maxp ? maxp : 1) + 1));
    sys->cap_buses = nbus + 1;
    sys->buses = (sa_bus *)calloc((size_t)sys->cap_buses, sizeof(sa_bus));
    sys->busvols = (sa_busvol *)calloc((size_t)(nbv ? nbv : 1), sizeof(sa_busvol));
    sys->curvepts = (sa_curvept *)calloc((size_t)(ncurve ? ncurve : 1), sizeof(sa_curvept));
    sys->relpts = (float *)calloc((size_t)(nrel ? nrel : 1) * 2, sizeof(float));
    sys->spatials = (sa_spatial *)calloc((size_t)(nsp ? nsp : 1), sizeof(sa_spatial));
    sys->h_bus_mask = pow2_at_least((uint32_t)sys->cap_buses * 2) - 1;
    sys->h_bus = (int32_t *)calloc(sys->h_bus_mask + 1, sizeof(int32_t));
    sys->h_event_mask = pow2_at_least((uint32_t)(nev ? nev : 1) * 2) - 1;
    sys->h_event_path = (int32_t *)calloc(sys->h_event_mask + 1, sizeof(int32_t));
    sys->h_event_guid = (int32_t *)calloc(sys->h_event_mask + 1, sizeof(int32_t));
    if (!sys->strpool || !sys->events || !sys->takes || !sys->take_order || !sys->groups || !sys->params || !sys->pvals ||
        !sys->buses || !sys->busvols || !sys->curvepts || !sys->h_bus || !sys->h_event_path || !sys->h_event_guid ||
        !sys->relpts || !sys->spatials) { rc = SA_ERR_NO_MEMORY; goto done; }
    sys->max_event_params = maxp;

    /* ---- buses ---- */
    bus_get(sys, "bus:/");
    if (jb && jb->type == SA_J_ARR) for (const sa_json *b = jb->child; b; b = b->next) {
        const char *p = sa_json_str(sa_json_get(b, "path"), NULL);
        if (!p) continue;
        int bi = bus_get(sys, p);
        float dv = (float)sa_json_num(sa_json_get(b, "defaultVolume"), 1.0);
        if (dv < 0) dv = powf(10.f, dv / 20.f);   /* negative = dB */
        sys->buses[bi].def_volume = dv;
    }

    /* ---- events ---- */
    if (je) for (const sa_json *e = je->child; e; e = e->next) {
        const char *p = sa_json_str(sa_json_get(e, "path"), NULL);
        if (!p) { if (strict) { snprintf(err, errlen, "event without path"); rc = SA_ERR_FORMAT; goto done; } continue; }
        if (sa_find_event(sys, p) >= 0) { if (strict) { snprintf(err, errlen, "duplicate event %s", p); rc = SA_ERR_FORMAT; goto done; } continue; }
        int ei = sys->nevents;
        sa_event *ev = &sys->events[ei];
        memset(ev, 0, sizeof *ev);
        ev->path = pool_str(sys, p, NULL);
        const char *g = sa_json_str(sa_json_get(e, "guid"), NULL);
        if (g && sa_parse_guid(g, ev->guid) && strict) { snprintf(err, errlen, "bad guid in %s", p); rc = SA_ERR_FORMAT; goto done; }
        ev->oneshot = sa_json_bool(sa_json_get(e, "oneshot"), 1);
        const sa_json *lm = sa_json_get(e, "lengthMs");
        ev->length_ms = lm && lm->type == SA_J_NUM ? (int32_t)lm->num : -1;
        ev->is3d = sa_json_bool(sa_json_get(e, "is3D"), 0);
        ev->min_dist = (float)sa_json_num(sa_json_get(e, "minDistance"), 1.0);
        ev->max_dist = (float)sa_json_num(sa_json_get(e, "maxDistance"), 20.0);
        ev->is_snapshot = !strncmp(p, "snapshot:/", 10) || !strncmp(p, "SNAPSHOT:/", 10);
        const char *bp = sa_json_str(sa_json_get(e, "bus"), NULL);
        ev->bus = bp && *bp ? bus_get(sys, bp) : 0;
        ev->fade_in_ms = (float)sa_json_num(sa_json_get(e, "fadeInMs"), -1);
        ev->fade_out_ms = (float)sa_json_num(sa_json_get(e, "fadeOutMs"), -1);
        ev->spatial = -1; ev->silent_group = -1; ev->silent_stop_frames = -1;
        parse_release(sys, ev, e, (float)sys->cfg.output_rate);
        if (ev->is3d) parse_spatial(sys, ev, e);
        /* params */
        ev->first_param = sys->nparams;
        const sa_json *ps = sa_json_get(e, "params");
        if (ps && ps->type == SA_J_ARR) for (const sa_json *q = ps->child; q; q = q->next) {
            const char *n = sa_json_str(sa_json_get(q, "name"), NULL);
            if (!n) continue;
            sa_param_def *pd = &sys->params[sys->nparams++];
            pd->name = pool_str(sys, n, NULL);
            pd->min = (float)sa_json_num(sa_json_get(q, "min"), 0);
            pd->max = (float)sa_json_num(sa_json_get(q, "max"), 1);
            pd->def = (float)sa_json_num(sa_json_get(q, "default"), pd->min);
            pd->global = sa_json_bool(sa_json_get(q, "global"), 0);
            { const sa_json *lb = sa_json_get(q, "labels"); pd->discrete = sa_json_bool(sa_json_get(q, "discrete"), 0) || (lb && lb->type == SA_J_ARR); }
            pd->gindex = -1;
            if (pd->global) {
                int existed = sa_global_index(sys, n, 0) >= 0;
                pd->gindex = sa_global_index(sys, n, 1);
                if (pd->gindex >= 0 && !existed) sys->gvals[pd->gindex] = pd->def;
            }
            ev->nparams++;
        }
        /* takes */
        int t0 = sys->ntakes;
        const sa_json *ts = sa_json_get(e, "takes");
        if (ts && ts->type == SA_J_ARR) for (const sa_json *t = ts->child; t; t = t->next) {
            const char *f = sa_json_str(sa_json_get(t, "file"), NULL);
            if (!f) continue;
            int codec = codec_of(sa_json_str(sa_json_get(t, "codec"), NULL));
            if (!codec) {
                if (strict) { snprintf(err, errlen, "unknown codec in %s", p); rc = SA_ERR_FORMAT; goto done; }
                continue;
            }
            sa_take *tk = &sys->takes[sys->ntakes];
            memset(tk, 0, sizeof *tk);
            tk->file = is_abs(f) ? pool_str(sys, f, NULL) : pool_str(sys, sys->base_dir, f);
            tk->codec = codec;
            tk->channels = (int)sa_json_num(sa_json_get(t, "channels"), 2);
            if (tk->channels < 1 || tk->channels > 2) tk->channels = 2;
            tk->rate = (int)sa_json_num(sa_json_get(t, "rate"), set_rate);
            if (tk->rate < 1000 || tk->rate > 192000) tk->rate = set_rate;
            double fr = sa_json_num(sa_json_get(t, "frames"), 0);
            tk->frames = fr > 0 ? (uint32_t)fr : 0;
            tk->loop_start = tk->loop_end = -1;
            const sa_json *lp = sa_json_get(t, "loop");
            if (lp && lp->type == SA_J_OBJ) {
                double ls = sa_json_num(sa_json_get(lp, "start"), -1), le = sa_json_num(sa_json_get(lp, "end"), -1);
                if (ls >= 0 && le > ls) { tk->loop_start = (int32_t)ls; tk->loop_end = (int32_t)le; }
                if (tk->frames && tk->loop_end > (int32_t)tk->frames) tk->loop_end = (int32_t)tk->frames;
                if (tk->loop_end <= tk->loop_start) tk->loop_start = tk->loop_end = -1;
            }
            tk->weight = (float)sa_json_num(sa_json_get(t, "weight"), 1);
            if (!(tk->weight > 0)) tk->weight = 1;
            tk->priming = (int32_t)sa_json_num(sa_json_get(t, "priming"), sa_json_num(sa_json_get(t, "encoderDelay"), codec == SA_CODEC_AAC ? SA_AAC_PRIMING_DEFAULT : 0));
            tk->event = ei;
            {   /* FMOD's STOPPED for this render (one-shot-like takes only) */
                const sa_json *sf = sa_json_get(t, "stopFrame");
                tk->stop_frame = sf && sf->type == SA_J_NUM && sf->num >= 0 && tk->loop_start < 0 ? (int32_t)sf->num : -1;
            }
            tk->pv_first = -1;
            const sa_json *pv = sa_json_get(t, "paramValues");
            if (pv && pv->type == SA_J_OBJ && ev->nparams > 0) {
                tk->pv_first = sys->npvals;
                float *dst = sys->pvals + sys->npvals;
                for (int k = 0; k < ev->nparams; k++) dst[k] = NAN;
                int any = 0;
                for (const sa_json *q = pv->child; q; q = q->next) {
                    for (int k = 0; k < ev->nparams; k++)
                        if (sa_strieq(sys->params[ev->first_param + k].name, q->key)) { dst[k] = (float)sa_json_num(q, 0); any = 1; }
                }
                if (any) sys->npvals += ev->nparams; else tk->pv_first = -1;
            }
            if (codec == SA_CODEC_AAC) ev->streamed_any = 1;
            if (tk->loop_start >= 0) ev->loops_any = 1;
            sys->ntakes++;
        }
        /* group takes by identical paramValues */
        ev->first_take = t0; ev->ntakes = sys->ntakes - t0;
        ev->first_group = sys->ngroups; ev->ngroups = 0;
        {
            int placed = 0;
            for (int a = t0; a < sys->ntakes; a++) sys->takes[a].group = -1;
            for (int a = t0; a < sys->ntakes; a++) {
                if (sys->takes[a].group >= 0) continue;
                sa_group *gr = &sys->groups[sys->ngroups];
                gr->first = t0 + placed; gr->count = 0; gr->weight_sum = 0; gr->nspec = 0;
                const float *pa = sys->takes[a].pv_first >= 0 ? sys->pvals + sys->takes[a].pv_first : NULL;
                if (pa) for (int k = 0; k < ev->nparams; k++) if (pa[k] == pa[k]) gr->nspec++;
                for (int b = a; b < sys->ntakes; b++) {
                    if (sys->takes[b].group >= 0) continue;
                    const float *pb = sys->takes[b].pv_first >= 0 ? sys->pvals + sys->takes[b].pv_first : NULL;
                    if (!same_pv(pa, pb, ev->nparams)) continue;
                    sys->takes[b].group = sys->ngroups - ev->first_group;
                    sys->take_order[t0 + placed++] = b;
                    gr->count++; gr->weight_sum += sys->takes[b].weight;
                }
                sys->ngroups++; ev->ngroups++;
            }
            ev->first_take = t0;   /* take_order[first_take .. +ntakes] grouped */
            for (int gi = 0; gi < ev->ngroups; gi++) {
                const sa_take *gt = &sys->takes[sys->take_order[sys->groups[ev->first_group + gi].first]];
                if (gt->pv_first < 0) continue;
                for (int k = 0; k < ev->nparams; k++) if (sys->pvals[gt->pv_first + k] == sys->pvals[gt->pv_first + k]) sys->params[ev->first_param + k].nvariants++;
            }
            /* the silent group: FMOD renders nothing at the defaults (draw_power_loop at power 0, river_loop, ...). The bake
             * says so ("silentAtDefault"); a manifest without the flag is read by the same contract: paramValues null = all
             * defaults, and the bake only leaves that group out when it rendered silence */
            int need = 0;
            if (ev->nparams > 0 && ev->ntakes > 0) {
                if (sa_json_bool(sa_json_get(e, "silentAtDefault"), 0)) need = 1;
                else {
                    int any_default = 0;
                    for (int gi = 0; gi < ev->ngroups && !any_default; gi++) {
                        const sa_take *gt = &sys->takes[sys->take_order[sys->groups[ev->first_group + gi].first]];
                        const float *pv = gt->pv_first >= 0 ? sys->pvals + gt->pv_first : NULL;
                        int at = 1;
                        for (int k = 0; k < ev->nparams && pv; k++)
                            if (pv[k] == pv[k] && fabsf(pv[k] - sys->params[ev->first_param + k].def) > 1e-4f) at = 0;
                        any_default = at;
                    }
                    need = !any_default;
                }
            }
            if (need) {
                sa_group *gr = &sys->groups[sys->ngroups];
                gr->first = -1; gr->count = 0; gr->weight_sum = 0; gr->nspec = 0;
                ev->silent_group = ev->ngroups;
                sys->ngroups++; ev->ngroups++;
                const sa_json *ss = sa_json_get(e, "silentStopFrame");
                if (ss && ss->type == SA_J_NUM && ss->num >= 0) ev->silent_stop_frames = (int32_t)(ss->num * (double)sys->cfg.output_rate / (double)set_rate);
            }
        }
        sys->nevents++;
        event_hash_insert(sys, ei);
    }

    /* ---- snapshots ---- */
    if (js && js->type == SA_J_ARR) for (const sa_json *s = js->child; s; s = s->next) {
        const char *p = sa_json_str(sa_json_get(s, "path"), NULL);
        if (!p) continue;
        int ei = sa_find_event(sys, p);
        sa_event *ev;
        if (ei < 0) {
            ei = sys->nevents++;
            ev = &sys->events[ei];
            memset(ev, 0, sizeof *ev);
            ev->path = pool_str(sys, p, NULL);
            const char *g = sa_json_str(sa_json_get(s, "guid"), NULL);
            if (g) sa_parse_guid(g, ev->guid);
            ev->length_ms = -1; ev->min_dist = 1; ev->max_dist = 20;
            ev->spatial = -1; ev->silent_group = -1; ev->silent_stop_frames = -1;
            ev->first_take = sys->ntakes; ev->first_group = sys->ngroups; ev->first_param = sys->nparams;
            ev->fade_in_ms = (float)sa_json_num(sa_json_get(s, "fadeInMs"), -1);
            ev->fade_out_ms = (float)sa_json_num(sa_json_get(s, "fadeOutMs"), -1);
            event_hash_insert(sys, ei);
        } else ev = &sys->events[ei];
        ev->is_snapshot = 1; ev->oneshot = 0; ev->bus = -1;
        ev->first_busvol = sys->nbusvols; ev->nbusvol = 0;
        const sa_json *bv = sa_json_get(s, "busVolumes");
        if (bv && bv->type == SA_J_OBJ) for (const sa_json *q = bv->child; q; q = q->next) {
            sa_busvol *v = &sys->busvols[sys->nbusvols++];
            v->bus = bus_get(sys, q->key);
            v->gain = (float)sa_json_num(q, 1);
            v->db = to_db(v->gain);
            ev->nbusvol++;
        }
        const sa_json *ps = sa_json_get(s, "params");
        if (ps && ps->type == SA_J_ARR && ev->nparams == 0) {
            ev->first_param = sys->nparams;
            for (const sa_json *q = ps->child; q; q = q->next) {
                const char *n = sa_json_str(sa_json_get(q, "name"), NULL);
                if (!n) continue;
                sa_param_def *pd = &sys->params[sys->nparams++];
                memset(pd, 0, sizeof *pd);
                pd->name = pool_str(sys, n, NULL);
                pd->min = (float)sa_json_num(sa_json_get(q, "min"), 0);
                pd->max = (float)sa_json_num(sa_json_get(q, "max"), 1);
                pd->def = (float)sa_json_num(sa_json_get(q, "default"), pd->max);
                pd->gindex = -1;
                ev->nparams++;
            }
        }
        ev->first_curve = sys->ncurvepts; ev->ncurve = 0;
        const sa_json *cv = sa_json_get(s, "curve");
        if (cv && cv->type == SA_J_ARR) for (const sa_json *c = cv->child; c; c = c->next) {
            const sa_json *pv = sa_json_get(c, "paramValues"), *cb = sa_json_get(c, "busVolumes");
            if (!pv || pv->type != SA_J_OBJ || !cb || cb->type != SA_J_OBJ) continue;
            for (const sa_json *q = pv->child; q; q = q->next) {
                int k = -1;
                for (int z = 0; z < ev->nparams; z++) if (sa_strieq(sys->params[ev->first_param + z].name, q->key)) k = z;
                if (k < 0) continue;
                sa_curvept *cp = &sys->curvepts[sys->ncurvepts++];
                cp->param = k; cp->value = (float)sa_json_num(q, 0);
                cp->first_busvol = sys->nbusvols; cp->nbusvol = 0;
                for (const sa_json *b = cb->child; b; b = b->next) {
                    sa_busvol *v = &sys->busvols[sys->nbusvols++];
                    v->bus = bus_get(sys, b->key); v->gain = (float)sa_json_num(b, 1); v->db = to_db(v->gain);
                    cp->nbusvol++;
                }
                ev->ncurve++;
            }
        }
    }
    if (!sys->nevents && strict) { snprintf(err, errlen, "manifest has no events"); rc = SA_ERR_FORMAT; }
done:
    sa_json_free(&arena);
    free(text);
    return rc;
}

void sa_manifest_free(sa_system *sys)
{
    free(sys->strpool); free(sys->events); free(sys->takes); free(sys->take_order); free(sys->groups); free(sys->params);
    free(sys->pvals); free(sys->buses); free(sys->busvols); free(sys->curvepts); free(sys->h_bus); free(sys->h_event_path); free(sys->h_event_guid);
    free(sys->relpts); free(sys->spatials);
    for (int i = 0; i < sys->nglobals; i++) free(sys->gnames[i]);
    free(sys->gnames); free(sys->gvals); free(sys->base_dir);
}
