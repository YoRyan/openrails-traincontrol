# PRR Cab Signaling

Simulates the cab signaling system used by the Pennsylvania Railroad, intended
for use on Vince Cockeram's PRR Eastern Region route.

#### Installation

After copying this directory to the `Common.Script` folder, determine whether or
not the locomotive should be equipped with automatic speed control, the
subsystem that can apply the brakes if the engineer does not respond to a signal
change. Generally, the PRR's electric and passenger diesel locomotives were so
equipped, while their freight diesel and steam locomotives were not.

For a locomotive with speed control, add the following to the .eng file:

```
Engine ( ...
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\YoRyan_PRR_CSS.cs" )
    ORTSTrainControlSystemParameters ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\WithSpeedControl.ini" )
    ORTSTrainControlSystemSound ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\Sounds.sms" )
    ...
)
```

For a locomotive without speed control, add the following instead:

```
Engine ( ...
    ORTSTrainControlSystem ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\YoRyan_PRR_CSS.cs" )
    ORTSTrainControlSystemParameters ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\WithoutSpeedControl.ini" )
    ORTSTrainControlSystemSound ( "..\\..\\Common.Script\\YoRyan_PRR_CSS\\Sounds.sms" )
    ...
)
```

If you have the beta 2 version of Vince Cockeram's PRR Eastern Region
mini-route, you can use the included conversion kit to outfit all locomotives
(that have a player-driveable consist) with functional cab signaling.

#### Overview

The PRR's cab signaling system used pulse codes and was capable of communicating
four aspects:

| Aspect | Meaning |
| --- | --- |
| Clear | (no restriction) |
| Approach Medium | Slow to 45 mph, then pass the next signal at 30 mph |
| Approach | Slow to 30 mph, then prepare to stop at the next signal |
| Restricting | Slow to 20 mph, then prepare to stop |

The script modifies the "next signal aspect" display in the cab to instead
display the aspect currently *in effect*, as cab signals do in real-world
operation.

The script also imposes the "Restricting" aspect when the train is half a signal
block away from any signal indicating Stop. This emulates the code change points
that were placed ahead of interlockings by the real PRR.

When the cab signal system imposes a more restrictive aspect, the peanut whistle
tone will play, and you have 6 seconds to acknowledge (**Z**) this alarm. Then,
you have another 6 seconds to move the brake handle to Suppression and begin to
slow the train's speed below the signal speed. Failure to perform any of
these actions will result in a penalty brake application.

To recover from a penalty brake, wait until the train comes to a complete stop
and press **Z**. Then, you may release the brakes and restart the train.

#### Script parameters

The two included .ini files turn the automatic speed control on and off.

```
[CSS]
; Enable/disable automatic speed control.
SpeedControl=true

[Alerter]
; Countdown time until the alerter activates (seconds)
CountdownTimeS=60
; Does manipulating the controls stop the countdown?
DoControlsReset=false
```

#### Release notes
