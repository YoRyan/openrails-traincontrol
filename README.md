This repository is where I develop Train Control System (TCS) scripts for
[Open Rails](http://openrails.org/) routes and equipment I am interested in.
TCS scripts can simulate all sorts of railway safety systems, making the train
driving experience more realistic, engaging, and enjoyable.

TCS scripts must be installed on each individual locomotive. To install a
script:

1. Create the `TRAINSET\Common.Script` folder if it does not already exist.
2. Copy the directory containing the script (for example, `YoRyan_BNSF_ATS`) to
   this folder.
3. In the .eng file of the locomotive, add the following parameter to the
   `Engine` section:

        Engine ( ...
            ORTSTrainControlSystem ( "..\\..\\Common.Script\\<script>\\<script>.cs" )
            ...
        )

   where `<script>` is the name of the script, such as `YoRyan_BNSF_ATS`.
4. To test the installation, enable logging within Open Rails and check the log
   file after startup. You should see a startup message like "ATS initialized".

Instead of modifying the .eng file, you could also Open Rails' include system to
inject the new parameter. To do this, create an `OpenRails` directory in the
engine folder. Then, create a .eng file with the same filename as the .eng file
you would otherwise modify. Fill it with these contents:

```

include ( "../<eng filename>" )
Engine (
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\<script>\\<script>.cs" )
)
```

where `<eng filename>` is the filename of the original .eng file. Note the blank
line before the `include` statement and the use of forward slashes and
backslashes.
