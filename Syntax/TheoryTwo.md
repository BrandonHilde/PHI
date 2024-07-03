# Theory




### SYNTAX THEORY #2

```
    // Memory safe variables
    //public varables start with upercase and no underscore
    var Greetings = 'hello, world';
    int Age = 30;

    // Unsafe public variable examples
    string _Name = 'Brandon';
    // Unsafe non-public examples start with lowercase and underscore
    string _birthday = 'Dec 29';
    int _birthyear = 1993;

    // memory safe start without underscore _
    string arrayOne:3;
    string testArray: = { 'one', 'two', 'three' };

    log 'Name: ': Name : _birthyear; // print string

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
