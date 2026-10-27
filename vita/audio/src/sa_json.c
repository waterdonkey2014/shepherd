/* Minimal in-situ JSON parser (RFC 8259 subset sufficient for the manifest and scenario scripts). */
#include "sa_internal.h"

#define SA_JBLOCK_NODES 4096
struct sa_jblock { struct sa_jblock *next; int used; sa_json nodes[SA_JBLOCK_NODES]; };

typedef struct jp { char *p, *end; sa_jarena *a; char *err; size_t errlen; int depth; } jp;

static sa_json *jnew(jp *j)
{
    struct sa_jblock *b = j->a->head;
    if (!b || b->used == SA_JBLOCK_NODES) {
        b = (struct sa_jblock *)malloc(sizeof *b);
        if (!b) return NULL;
        b->next = j->a->head; b->used = 0; j->a->head = b;
    }
    sa_json *n = &b->nodes[b->used++];
    memset(n, 0, sizeof *n);
    return n;
}
static int jerr(jp *j, const char *msg)
{
    if (j->err && j->errlen) snprintf(j->err, j->errlen, "json: %s at byte offset ~%ld", msg, (long)(j->end - j->p));
    return -1;
}
static void ws(jp *j) { while (j->p < j->end && (*j->p == ' ' || *j->p == '\t' || *j->p == '\n' || *j->p == '\r')) j->p++; }
static int hex4(const char *s, unsigned *out)
{
    unsigned v = 0;
    for (int i = 0; i < 4; i++) {
        char c = s[i]; v <<= 4;
        if (c >= '0' && c <= '9') v |= (unsigned)(c - '0');
        else if (c >= 'a' && c <= 'f') v |= (unsigned)(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') v |= (unsigned)(c - 'A' + 10);
        else return -1;
    }
    *out = v; return 0;
}
static char *put_utf8(char *d, unsigned cp)
{
    if (cp < 0x80) *d++ = (char)cp;
    else if (cp < 0x800) { *d++ = (char)(0xC0 | (cp >> 6)); *d++ = (char)(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) { *d++ = (char)(0xE0 | (cp >> 12)); *d++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *d++ = (char)(0x80 | (cp & 0x3F)); }
    else { *d++ = (char)(0xF0 | (cp >> 18)); *d++ = (char)(0x80 | ((cp >> 12) & 0x3F)); *d++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *d++ = (char)(0x80 | (cp & 0x3F)); }
    return d;
}
/* parses a string starting at the opening quote; decodes in place; returns start pointer */
static char *jstring(jp *j)
{
    if (j->p >= j->end || *j->p != '"') { jerr(j, "expected string"); return NULL; }
    char *s = ++j->p, *d = s;
    while (j->p < j->end) {
        char c = *j->p++;
        if (c == '"') { *d = 0; return s; }
        if (c == '\\') {
            if (j->p >= j->end) break;
            char e = *j->p++;
            switch (e) {
            case '"': *d++ = '"'; break; case '\\': *d++ = '\\'; break; case '/': *d++ = '/'; break;
            case 'b': *d++ = '\b'; break; case 'f': *d++ = '\f'; break; case 'n': *d++ = '\n'; break;
            case 'r': *d++ = '\r'; break; case 't': *d++ = '\t'; break;
            case 'u': {
                unsigned cp, lo;
                if (j->end - j->p < 4 || hex4(j->p, &cp)) { jerr(j, "bad \\u escape"); return NULL; }
                j->p += 4;
                if (cp >= 0xD800 && cp < 0xDC00 && j->end - j->p >= 6 && j->p[0] == '\\' && j->p[1] == 'u' && !hex4(j->p + 2, &lo) && lo >= 0xDC00 && lo < 0xE000) {
                    cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00); j->p += 6;
                }
                d = put_utf8(d, cp); break;
            }
            default: jerr(j, "bad escape"); return NULL;
            }
        } else *d++ = c;
    }
    jerr(j, "unterminated string");
    return NULL;
}
static sa_json *jvalue(jp *j);
static sa_json *jvalue(jp *j)
{
    ws(j);
    if (j->p >= j->end) { jerr(j, "unexpected end"); return NULL; }
    if (++j->depth > 64) { jerr(j, "too deep"); return NULL; }
    sa_json *n = jnew(j);
    if (!n) { jerr(j, "out of memory"); return NULL; }
    char c = *j->p;
    if (c == '{' || c == '[') {
        int obj = c == '{';
        n->type = obj ? SA_J_OBJ : SA_J_ARR;
        j->p++;
        sa_json **tail = &n->child;
        ws(j);
        if (j->p < j->end && *j->p == (obj ? '}' : ']')) { j->p++; j->depth--; return n; }
        for (;;) {
            const char *key = NULL;
            ws(j);
            if (obj) {
                key = jstring(j); if (!key) return NULL;
                ws(j);
                if (j->p >= j->end || *j->p != ':') { jerr(j, "expected ':'"); return NULL; }
                j->p++;
            }
            sa_json *v = jvalue(j); if (!v) return NULL;
            v->key = key; *tail = v; tail = &v->next; n->count++;
            ws(j);
            if (j->p < j->end && *j->p == ',') { j->p++; continue; }
            if (j->p < j->end && *j->p == (obj ? '}' : ']')) { j->p++; break; }
            jerr(j, obj ? "expected ',' or '}'" : "expected ',' or ']'"); return NULL;
        }
    } else if (c == '"') {
        n->type = SA_J_STR; n->str = jstring(j); if (!n->str) return NULL;
    } else if (c == 't' && j->end - j->p >= 4 && !memcmp(j->p, "true", 4)) { n->type = SA_J_BOOL; n->num = 1; j->p += 4; }
    else if (c == 'f' && j->end - j->p >= 5 && !memcmp(j->p, "false", 5)) { n->type = SA_J_BOOL; n->num = 0; j->p += 5; }
    else if (c == 'n' && j->end - j->p >= 4 && !memcmp(j->p, "null", 4)) { n->type = SA_J_NULL; j->p += 4; }
    else if (c == '-' || (c >= '0' && c <= '9')) {
        char buf[64]; int k = 0;
        while (j->p < j->end && k < 63 && *j->p && strchr("+-0123456789.eE", *j->p)) buf[k++] = *j->p++;
        buf[k] = 0;
        char *e; n->num = strtod(buf, &e);
        if (e == buf) { jerr(j, "bad number"); return NULL; }
        n->type = SA_J_NUM;
    } else { jerr(j, "unexpected character"); return NULL; }
    j->depth--;
    return n;
}

sa_json *sa_json_parse(char *text, size_t len, sa_jarena *arena, char *err, size_t errlen)
{
    jp j; j.p = text; j.end = text + len; j.a = arena; j.err = err; j.errlen = errlen; j.depth = 0;
    if (len >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB && (unsigned char)text[2] == 0xBF) j.p += 3;
    sa_json *v = jvalue(&j);
    if (!v) return NULL;
    ws(&j);
    if (j.p != j.end && *j.p) { jerr(&j, "trailing data"); return NULL; }
    return v;
}
void sa_json_free(sa_jarena *arena)
{
    struct sa_jblock *b = arena->head;
    while (b) { struct sa_jblock *n = b->next; free(b); b = n; }
    arena->head = NULL;
}
const sa_json *sa_json_get(const sa_json *obj, const char *key)
{
    if (!obj || obj->type != SA_J_OBJ) return NULL;
    for (const sa_json *c = obj->child; c; c = c->next) if (c->key && !strcmp(c->key, key)) return c;
    return NULL;
}
double sa_json_num(const sa_json *v, double def) { return v && (v->type == SA_J_NUM || v->type == SA_J_BOOL) ? v->num : def; }
const char *sa_json_str(const sa_json *v, const char *def) { return v && v->type == SA_J_STR ? v->str : def; }
int sa_json_bool(const sa_json *v, int def)
{
    if (!v) return def;
    if (v->type == SA_J_BOOL || v->type == SA_J_NUM) return v->num != 0;
    return def;
}
char *sa_read_file(const char *path, size_t *len)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    long n = ftell(f);
    if (n < 0) { fclose(f); return NULL; }
    fseek(f, 0, SEEK_SET);
    char *b = (char *)malloc((size_t)n + 1);
    if (!b) { fclose(f); return NULL; }
    size_t got = fread(b, 1, (size_t)n, f);
    fclose(f);
    b[got] = 0;
    if (len) *len = got;
    return b;
}
