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

    log 'Name: '. Name; //print string

    // if statement example
    if Greetings == 'hello, world';

    log 'Greetings message hasn't changed'; // "'" denotes start "';" or "'." denotes end so "hasn't doesn't need "\"
    ;;
    else
          log 'Greetings message has changed';
    ;;


    // function that returns a value
    [ConvertToSeconds]

    float Minutes = 12; //first parameter with default value

    :: // start of function

    float Seconds = Minutes * 60;

    [return Seconds]

    //function that doesn't return a value

    [UpdateGreetings]

    string NewGreeting = '';

    ::Greetings = NewGreeting;

    [end]

    //class test
    [TestClass]

    safe string days: = {'Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday'};

    ::

        [ListDays]//function inside class

        ::

        //foreach loop style
        for days:i
            log days:i;    
        ;; //end loop or other enclosing condition

        //traditional for loop
        for int i = 0; i < days.length; i++;
            log days:i;
        ;; 

        // while loop
        for Age == 30;
            log 'Still: '. Age;
        ;;

        [end]


    [classend]
    


    // ASM integration 

    ASM.
    {
        global _start
        _start:
            mov rax, 60
            mov rdi, 72
            syscall
    }

    C. 
    {
        // maybe allow for other language integration using .LANGUAGE {}
    }

    ASM:_start; // call ASM from PHI



```