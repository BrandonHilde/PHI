# Theory




### SYNTAX THEORY #2

```
    # <- single line comment
    #
        Multiline
        Comment
    #

    # Memory safe variables
    #public varables start with upercase and no underscore
    var Greetings: 'hello, world';
    int Age: 30;

    # Unsafe public variable examples
    str _Name: 'Brandon';
    # Unsafe non-public examples start with lowercase and underscore
    str _birthday: 'Dec 29';
    int _birthyear: 1993;

    # memory safe start without underscore _  
    str arrayOne[3];
    int numberArray: 1 3 5 9 2;
    str testArray: 'one' 'two' 'three';

    log 'Name: ' Name _birthyear; # print string 

    # if statement example
    if Greetings == 'hello, world';

        log 'Greetings message hasn't changed'; # "'" denotes start "';" or "'." denotes end so "hasn't doesn't need "\"
    ;;
    else
          log 'Greetings message has changed';
    ;;


    # function that returns a value
    [ConvertToSeconds:
        dec Minutes = 12;
    ]

        dec Seconds = Minutes * 60;

    [end: Seconds]

    #function that doesn't return a value

    [UpdateGreetings:str nGreeting: '']
        
        Greetings = nGreeting;

    [end]

    #class test
    phi.TestClass
    { 
            str days:

                'Monday'
                'Tuesday'
                'Wednesday'
                'Thursday'
                'Friday'
                'Saturday'
                'Sunday';

        [phi.TestClass] # constructor

        [end]
    

        [ListDays] # function inside class

            # foreach loop style
            while days:i
                log days:i;    
            ;; # end loop or other enclosing condition

            # traditional for loop
            while int i = 0; i < days.length; i++;
                log days:i;
            ;; 

            # while loop
            while Age == 30;
                log 'Still: ' Age;
            ;;

        [end]
    }


    # ASM integration 

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
        # maybe allow for other language integration using .LANGUAGE {}
    }

    asm._start; # call ASM from PHI



```
