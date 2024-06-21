# Theory


### SYNTAX THEORY #1

```
    // Memory safe variables 
    public safe var Greetings = 'hello, world';
    public safe int Age = 30;

    // Unsafe variable examples
    public string Name = 'Brandon';
    // Unsafe non-public examples
    string Birthday = 'Dec 29';
    int Birthyear = 1993;

    string TestArray:3 = { 'one', 'two', 'three' };

    log 'Name: ' . Name; //print string


    // function that returns a value
    [ConvertToSeconds]

    float Minutes = 12; //first parameter with default value

    : // start of function

    float Seconds = Minutes * 60;

    [return Seconds]

    //function that doesn't return a value

    [UpdateGreetings]

    string NewGreeting = '';

    :Greetings = NewGreeting;

    [end]


    // ASM integration 

    ASM.
    {
        global _start
        _start:
            mov rax, 60
            mov rdi, 72
            syscall
    }




```