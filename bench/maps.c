#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

/* C has no map, so this is the one a C program would write for itself: open addressing in a
   table whose size is a power of 2, a key's place from Fibonacci hashing, linear probing, and
   removal that moves the keys after it back (no tombstones), as PHI's Map does. */

struct slot {
    uint32_t key;
    uint32_t value;
    uint32_t used;
};

struct map {
    struct slot *slots;
    uint32_t size;      /* a power of 2 */
    uint32_t shift;     /* 32 minus the bits in size */
    uint32_t count;
};

static uint32_t place(const struct map *m, uint32_t key)
{
    return (key * 0x9E3779B1u) >> m->shift;
}

static void grow(struct map *m);

static void set(struct map *m, uint32_t key, uint32_t value)
{
    if ((m->count + 1) * 4 > m->size * 3) grow(m);
    uint32_t mask = m->size - 1;
    for (uint32_t p = place(m, key);; p = (p + 1) & mask) {
        struct slot *s = &m->slots[p];
        if (!s->used) {
            s->key = key;
            s->value = value;
            s->used = 1;
            m->count++;
            return;
        }
        if (s->key == key) {
            s->value = value;
            return;
        }
    }
}

static void grow(struct map *m)
{
    struct slot *old = m->slots;
    uint32_t old_size = m->size;
    m->size = old_size ? old_size * 2 : 16;
    m->shift = old_size ? m->shift - 1 : 28;
    m->slots = calloc(m->size, sizeof(struct slot));
    m->count = 0;
    for (uint32_t i = 0; i < old_size; i++)
        if (old[i].used) set(m, old[i].key, old[i].value);
    free(old);
}

static struct slot *find(const struct map *m, uint32_t key)
{
    if (m->size == 0) return NULL;
    uint32_t mask = m->size - 1;
    for (uint32_t p = place(m, key); m->slots[p].used; p = (p + 1) & mask)
        if (m->slots[p].key == key) return &m->slots[p];
    return NULL;
}

static uint32_t get(const struct map *m, uint32_t key, uint32_t otherwise)
{
    struct slot *s = find(m, key);
    return s ? s->value : otherwise;
}

static void remove_(struct map *m, uint32_t key)
{
    struct slot *s = find(m, key);
    if (!s) return;
    uint32_t mask = m->size - 1;
    uint32_t pos = (uint32_t)(s - m->slots);
    uint32_t next = (pos + 1) & mask;
    while (m->slots[next].used) {
        uint32_t home = place(m, m->slots[next].key);
        if (((next - home) & mask) >= ((next - pos) & mask)) {
            m->slots[pos] = m->slots[next];
            pos = next;
        }
        next = (next + 1) & mask;
    }
    m->slots[pos].used = 0;
    m->count--;
}

static uint32_t round_(uint32_t count)
{
    const uint32_t spread = 2654435761u;
    struct map m = {0};
    for (uint32_t i = 0; i < count; i++) set(&m, i * spread, i);
    uint32_t sum = 0;
    for (uint32_t i = 0; i < count; i++) sum += get(&m, i * spread, 0);
    for (uint32_t i = 0; i < count; i++)
        if (find(&m, (count + i) * spread)) sum += 1000000;
    for (uint32_t i = 0; i < count; i += 2) remove_(&m, i * spread);
    for (uint32_t i = 0; i < count; i++) sum += get(&m, i * spread, 0);
    sum += m.count;
    free(m.slots);
    return sum;
}

int main(void)
{
    uint32_t total = 0;
    for (int r = 0; r < 50; r++) total += round_(100000);
    printf("%u\n", total);
    return 0;
}
