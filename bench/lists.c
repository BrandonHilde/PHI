#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

struct node {
    uint32_t value;
    struct node *next;
};

static uint32_t round_(int count)
{
    struct node *head = NULL;
    for (int i = 0; i < count; i++) {
        struct node *node = calloc(1, sizeof(struct node));
        node->value = (uint32_t)i;
        node->next = head;
        head = node;
    }
    uint32_t sum = 0;
    for (struct node *walk = head; walk != NULL; walk = walk->next) sum += walk->value;
    while (head != NULL) {
        struct node *next = head->next;
        free(head);
        head = next;
    }
    return sum;
}

int main(void)
{
    uint32_t total = 0;
    for (int r = 0; r < 200; r++) total += round_(100000);
    printf("%u\n", total);
    return 0;
}
