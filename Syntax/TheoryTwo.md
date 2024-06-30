# Theory




### SYNTAX THEORY #2

```
    // Memory safe variables 
    public safe var Greetings = 'hello, world';
    public safe int Age = 30;

    // Unsafe variable examples
    public string Name = 'Brandon';
    // Unsafe non-public examples
    string Birthday = 'Dec 29';
    int Birthyear = 1993;

    string ArrayOne:3;
    string TestArray: = { 'one', 'two', 'three' };

    log 'Name: ': Name : Birthyear; // print string

    // if statement example
    if Greetings == 'hello, world';

        log 'Greetings message hasn't changed'; // "'" denotes start "';" or "'." denotes end so "hasn't doesn't need "\"
    ;;
    else
          log 'Greetings message has changed';
    ;;


    // function that returns a value
    [ConvertToSeconds:
        float Minutes = 12;
    ]

        float Seconds = Minutes * 60;

    [end: Seconds]

    //function that doesn't return a value

    [UpdateGreetings:string nGreeting = '']
        
        Greetings = nGreeting;

    [end]

    //class test
    phi.
    {
        TestClass: 
            safe string days: = 
            {
                'Monday',
                'Tuesday',
                'Wednesday',
                'Thursday',
                'Friday',
                'Saturday',
                'Sunday'
            };
    

        [ListDays]//function inside class

            //foreach loop style
            while days:i
                log days:i;    
            ;; //end loop or other enclosing condition

            //traditional for loop
            while int i = 0; i < days.length; i++;
                log days:i;
            ;; 

            // while loop
            while Age == 30;
                log 'Still: '. Age;
            ;;

        [end]
    }


    // ASM integration 

    asm.
    {
        global _start
        _start:
            mov ax, 60
            mov di, 72
            int 0x10
    }

    arm.
    {
        
    }

    c. 
    {
        // maybe allow for other language integration using .LANGUAGE {}
    }

    asm._start; // call ASM from PHI



```
