# BNSF Automatic Train Stop

Simulates the ATS system installed on the former main lines of the Santa Fe,
currently in use by Amtrak, Metrolink, Coaster, and the New Mexico Rail Runner.

The ATS system consists of trackside inductors placed in advance of signals and
permanent speed restrictions, much like the well-known British AWS system. These
inductors trip an alarm in the cab, which must be acknowledged by the engineer
within 8 seconds or else the ATS system will apply penalty braking.

No MSTS or Open Rails route models these inductors, so the script "places" them
at the following locations:

- Just before all signals on 40 mph or higher track. These activate only if
  their respective signals are displaying non-clear aspects.
- 2 miles before any speed reduction of 20 mph or greater magnitude.

These speeds and distances can be customized by .ini file.

In the event of a penalty brake application, the Pneumatic Control Switch (PCS)
will open, cutting power and applying full service braking. To reset the train,
acknowledge the alarm and move the train brake handle to Suppression for 10
seconds. When you see the "PCS: released" message, you may release the brakes
and get rolling again.

#### Script parameters

```
[ATS]
; Minimum line speed to place inductors at signals (miles per hour)
SignalActivateSpeedMPH=40
; Minimum speed limit decrease to place inductors before slow zones (miles per hour)
SpeedReductionDiffMPH=20
; Distance before qualifying speed drops to place inductors (miles)
SpeedReductionDistMi=2
```

#### Release notes

* v1.0 - May 2, 2020
  * Initial release