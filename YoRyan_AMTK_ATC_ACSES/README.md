# Amtrak Northeast Corridor

Simulates the PRR-derived Automatic Train Control (ATC) and Alstom-designed
Advanced Civil Speed Enforcement System (ACSES) systems in use on Amtrak trains
on the Northeast Corridor between Washington and Boston.

#### Video Demo

[![YouTube video](http://img.youtube.com/vi/SUGYjtR9lcY/0.jpg)](http://www.youtube.com/watch?v=SUGYjtR9lcY "YouTube video")

#### Installation

Copy this directory to the `Common.Script` folder.

To equip a locomotive with ATC and ACSES, add the following to the .eng file:

```
Engine ( ...
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\YoRyan_AMTK_ATC_ACSES.cs" )
    ORTSTrainControlSystemParameters ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\WithACSES.ini" )
    ORTSTrainControlSystemSound ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\Sounds.sms" )
    ...
)
```

To omit ACSES and use ordinary ATC, add the following instead:

```
Engine ( ...
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\YoRyan_AMTK_ATC_ACSES.cs" )
    ORTSTrainControlSystemParameters ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\WithoutACSES.ini" )
    ORTSTrainControlSystemSound ( "..\\..\\Common.Script\\YoRyan_AMTK_ATC_ACSES\\Sounds.sms" )
    ...
)
```

If you have the NEC pack from North American Locomotive Works, you can use the
included conversion kit to outfit all AEM-7 locomotives with ATC. It also equips
Phase V AEM-7's with ACSES.

Another conversion kit has been included to outfit the default Acela Express and
HHP-8 with both ATC and ACSES.

#### Overview

On the Northeast Corridor, ATC enforces cab signal speeds provided by pulse
codes that are largely compatible with those of the PRR, while ACSES enforces
permanent and temporary speed limits.

##### Cab displays

The information presented by ATC and ACSES must be routed to one of the
available in-cab displays. This is customizable in the .ini parameters file
according to the layout of the current locomotive and your own preferences.

The `SPEEDLIM_DISPLAY` (the cab signal speed limit; present in most cabs) and
`SPEEDLIMIT` (a dedicated speed limit display; not very common) displays support
the following modes:

| Mode | Displays |
| --- | --- |
| `atc` | ATC speed limit |
| `atc,acses` | Lower of the two ATC and ACSES speed limits |
| `acses` | ACSES speed limit |
| `acses,ttp` | ACSES speed limit, or Time to Penalty countdown if activated |
| `<blank>` | (nothing) |

The `Confirm` display represents a single-line popup that is similar to a cab
control status confirmation. Only one mode is suppported by this display:

| Mode | Displays |
| --- | --- |
| `ttp` | Time to Penalty countdown if activated |
| `<blank>` | (nothing) |

The default configuration is to combine both ATC and ACSES speed limits on the
`SPEEDLIM_DISPLAY` and to use the `Confirm` display for the Time to Penalty
countdown. This mimics the contemporary style of ADU (combined speeds, dedicated
Time to Penalty indicator) installed on Amtrak's current equipment and is
compatible with the widest range of MSTS and Open Rails stock.

If a cab is equipped with a `SPEEDLIMIT` display, an alternative would be to set
the `SPEEDLIM_DISPLAY` display to `atc` mode and the `SPEEDLIMIT` display to
`acses` mode. This would mimic the older style of ADU (separate ATC and ACSES
speeds) installed on Amtrak trains in the early 2000's.

##### ATC

The script modifies the "next signal aspect" display in the cab to instead
display the aspect currently *in effect*, as cab signals do in real-world
operation.

| Aspect | Meaning |
| --- | --- |
| Clear 150 | Clear, maximum speed 150 mph |
| Clear 125 | Clear, maximum speed 125 mph |
| Clear 100 | Clear, maximum speed 100 mph |
| Cab Speed 80 | Clear, maximum speed 80 mph |
| Cab Speed 60 | Clear, maximum speed 80 mph |
| Approach Medium | Slow to 45 mph, then pass the next signal at 30 mph |
| Approach | Slow to 30 mph, then prepare to stop at the next signal |
| Restricting | Slow to 20 mph, then prepare to stop |

When ATC upgrades the cab signal aspect, an informational tone will sound.

When ATC imposes a more restrictive aspect, an alarm will sound, and you have 6
seconds to acknowledge (**Z**) it. If the train is exceeding the new cab signal
speed, you must also move the train brake handle to Suppression to start slowing
it down. Failure to perform any of these actions will result in a penalty brake
application.

To recover from a penalty brake, wait until the train comes to a complete stop
and press **Z**. Then, you may release the brakes and restart the train.

##### ACSES

When the train encounters a higher speed limit, ACSES sounds an informational
tone and displays the new limit on the cab display. Unlike the track monitor,
ACSES does not wait until the rear of the train has passed the speedpost; it is
the engineer's responsibility to delay increasing the train's speed until the
entire length of the train has cleared the post.

As the train approaches a lower speed limit, ACSES computes a braking curve (the
"penalty curve") to determine the last possible moment at which it can intervene
to slow the train before it reaches the new limit. It also computes a gentler
curve (the "alert curve") 8 seconds in advance of the penalty curve.

If the train exceeds the alert curve, ACSES will display the lower speed limit
on the cab display and begin counting down the "Time to Penalty," or the
estimated amount of time until the train reaches the penalty curve. If the train
exceeds the penalty curve, ACSES will apply a full service brake that can be
released (**Z**) once the train's speed conforms to the new speed limit.

If the train reaches the lower speed limit without violating either braking
curves, ACSES simply displays the new limit and sounds an informational tone.

#### Script parameters

The two included .ini files turn ACSES on and off.

```
[ACSES]
; Enable/disable the Advanced Civil Speed Enforcement System
Enable=true

[Alerter]
; Countdown time until the alerter activates (seconds)
CountdownTimeS=60
; Once counting down, time until the penalty brake is applied (seconds)
AcknowledgeTimeS=10
; Does manipulating the controls stop the countdown?
DoControlsReset=true

[Displays]
; Route information to preferred cab displays
SPEEDLIMIT=
SPEEDLIM_DISPLAY=atc,acses
Confirm=ttp
```

#### Release notes

* v1.0 - June 27, 2020
  * Initial release
