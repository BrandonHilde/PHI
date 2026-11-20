/* The part of C's own library that every system PHI builds for has under the same names:
   written for vendor/bindgen.py to read (not to compile C with). A type whose size is the
   system's own is named here and given its PHI type in package.json. */
#include <stddef.h>

typedef struct FILE FILE;       /* held by its address only */
typedef ptrdiff_t phi_time;     /* time_t: as wide as an address on each system PHI builds for */

/* memory */
void *malloc(size_t size);                              // Room for size bytes, or 0
void *calloc(size_t count, size_t size);                // Room for count things of size bytes each, all zeros
void *realloc(void *memory, size_t size);               // The same bytes in room of another size (it may move)
void free(void *memory);                                // Gives back what malloc, calloc or realloc gave

/* text: bytes up to a 0 */
size_t strlen(const char *text);                        // How many bytes before the 0
char *strcpy(char *to, const char *from);               // Copies text, with its 0
char *strncpy(char *to, const char *from, size_t count); // Copies at most count bytes of text
char *strcat(char *to, const char *from);               // Adds text to the end of other text
char *strncat(char *to, const char *from, size_t count); // Adds at most count bytes of text
int strcmp(const char *a, const char *b);               // 0 when the same, below or above 0 by the first byte that differs
int strncmp(const char *a, const char *b, size_t count); // The same, over at most count bytes
char *strchr(const char *text, int byte);               // Where a byte first is in text, or 0
char *strrchr(const char *text, int byte);              // Where a byte last is in text, or 0
char *strstr(const char *text, const char *part);       // Where some text first is in other text, or 0
size_t strspn(const char *text, const char *among);     // How many bytes at the start are all among some
size_t strcspn(const char *text, const char *among);    // How many bytes at the start are none of some
char *strtok(char *text, const char *between);          // The next piece of text between any of some bytes
char *strerror(int error);                              // What an error's number means, as text

/* bytes */
void *memcpy(void *to, const void *from, size_t count);  // Copies count bytes (that don't overlap)
void *memmove(void *to, const void *from, size_t count); // Copies count bytes, right when they overlap
void *memset(void *to, int byte, size_t count);          // Sets count bytes to one value
int memcmp(const void *a, const void *b, size_t count);  // Compares count bytes, as strcmp does text
void *memchr(const void *bytes, int byte, size_t count); // Where a byte first is in count bytes, or 0

/* numbers and text */
int atoi(const char *text);                             // The whole number text starts with
double atof(const char *text);                          // The number text starts with
long strtol(const char *text, char **end, int base);    // A whole number in a base, and where it ended
unsigned long strtoul(const char *text, char **end, int base); // The same, never below 0
double strtod(const char *text, char **end);            // A number, and where it ended
int abs(int number);                                    // A number without its sign

/* sorting and searching: compare is a function given two elements' addresses */
void qsort(void *first, size_t count, size_t size, int (*compare)(const void *, const void *));     // Sorts count elements of size bytes each
void *bsearch(const void *key, const void *first, size_t count, size_t size, int (*compare)(const void *, const void *)); // Finds a key among sorted elements, or 0

/* the program and what's around it */
int rand(void);                                         // The next of a run of numbers that look random
void srand(unsigned seed);                              // Starts that run somewhere else
char *getenv(const char *name);                         // An environment variable's text, or 0
int system(const char *command);                        // Runs a command as the system's shell does
void exit(int code);                                    // Ends the program, with what C still holds written
phi_time time(phi_time *now);                           // Seconds since the start of 1970

/* files */
FILE *fopen(const char *path, const char *mode);        // Opens a file ("r", "w", "a", with "b" for bytes as they are), or 0
int fclose(FILE *file);                                 // Closes it, with what's waiting written
size_t fread(void *to, size_t size, size_t count, FILE *file);          // Reads count things of size bytes; how many it read
size_t fwrite(const void *from, size_t size, size_t count, FILE *file); // Writes count things of size bytes; how many it wrote
char *fgets(char *to, int room, FILE *file);            // Reads a line (at most room - 1 bytes), or 0 at the end
int fputs(const char *text, FILE *file);                // Writes text
int fgetc(FILE *file);                                  // Reads one byte, or -1 at the end
int fputc(int byte, FILE *file);                        // Writes one byte
int fseek(FILE *file, long offset, int from);           // Moves to an offset from the start (0), here (1) or the end (2)
long ftell(FILE *file);                                 // Where in the file it is
void rewind(FILE *file);                                // Back to the start
int fflush(FILE *file);                                 // Writes what's waiting (every file, given 0)
int feof(FILE *file);                                   // Whether the end has been read past
int ferror(FILE *file);                                 // Whether something went wrong with it
int remove(const char *path);                           // Deletes a file
int rename(const char *from, const char *to);           // Gives a file another name
int puts(const char *text);                             // Writes text and a new line to the program's output
int putchar(int byte);                                  // Writes one byte to the program's output
int printf(const char *format, ...);                    // Writes text made as format says (see C.printf_int and the others)
int sprintf(char *to, const char *format, ...);         // The same, into bytes of the program's own
int sscanf(const char *text, const char *format, ...);  // Reads values out of text as format says

/* what kind of character a byte is */
int isalpha(int byte);                                  // A letter
int isdigit(int byte);                                  // A digit
int isalnum(int byte);                                  // A letter or a digit
int isspace(int byte);                                  // A space, a tab or a new line
int isupper(int byte);                                  // A capital letter
int islower(int byte);                                  // A small letter
int ispunct(int byte);                                  // Punctuation
int isxdigit(int byte);                                 // A hexadecimal digit
int toupper(int byte);                                  // A letter as a capital
int tolower(int byte);                                  // A letter as a small one
