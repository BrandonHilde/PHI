/* C's maths (math.h), as every system PHI builds for has it: for vendor/bindgen.py to read. */
double sin(double x);                                   // Sine of an angle in radians
double cos(double x);                                   // Cosine of an angle in radians
double tan(double x);                                   // Tangent of an angle in radians
double asin(double x);                                  // The angle whose sine is x
double acos(double x);                                  // The angle whose cosine is x
double atan(double x);                                  // The angle whose tangent is x
double atan2(double y, double x);                       // The angle of the point (x, y), all the way round
double sinh(double x);                                  // Hyperbolic sine
double cosh(double x);                                  // Hyperbolic cosine
double tanh(double x);                                  // Hyperbolic tangent
double exp(double x);                                   // e to the power x
double log(double x);                                   // The natural logarithm
double log10(double x);                                 // The logarithm to base 10
double pow(double x, double y);                         // x to the power y
double sqrt(double x);                                  // The square root
double ceil(double x);                                  // The next whole number up
double floor(double x);                                 // The next whole number down
double fabs(double x);                                  // A number without its sign
double fmod(double x, double y);                        // What's left of x divided by y
double ldexp(double x, int power);                      // x times 2 to a power
double frexp(double x, int *power);                     // x as a fraction and a power of 2
double modf(double x, double *whole);                   // x's part after the point, and its whole part
