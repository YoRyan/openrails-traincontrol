This repository is where I develop Train Control System (TCS) scripts for
[Open Rails](http://openrails.org/) routes and equipment I am interested in.
TCS scripts can simulate all sorts of railway safety systems, making the train
driving experience more realistic, engaging, and enjoyable.

Use the "download ZIP" function to download the latest versions of the scripts.
There are no releases. **All scripts currently require Carlo Santucci's
[NewYear MG](http://www.elvastower.com/forums/index.php?/topic/32640-or-newyear-mg/)
fork of Open Rails, which can be obtained at
[his website](http://interazioni-educative.it/Downloads/index.php).**

TCS scripts must be installed on each individual locomotive. To install a
script:

1. Create the `TRAINSET\Common.Script` folder if it does not already exist.
2. Copy the directory containing the script (for example, `YoRyan_BNSF_ATS`) to
   this folder.
3. Modify the .eng file of the locomotive as instructed in the script's readme.
4. To test the installation, enable logging within Open Rails and check the log
   file after startup. You should see a startup message like "ATS initialized".

Instead of modifying the .eng file, you could also use Open Rails' include
system to overlay the new parameters. To do this, create an `OpenRails`
directory in the engine folder. Then, create a .eng file with the same filename
as the .eng file you would otherwise modify, and format it like so:

```

include ( "../<eng filename>" )
Engine (
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\<script>\\<script>.cs" )
    ...
)
```

where `<eng filename>` is the filename of the original .eng file. There *must*
be a blank line before the `include` statement. Also, take note of the
particular usage of forward slashes and backslashes.
