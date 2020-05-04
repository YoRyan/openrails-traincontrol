/*
 * PRR Cab Signalling System for Open Rails
 *
 * MIT License
 *
 * Copyright (c) 2020 Ryan Young <ryan@youngryan.com>
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using Orts.Simulation;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_PRR_CSS : TrainControlSystem
    {
        public const float CountdownSec = 6f;

        private BlockTracker blockTracker;
        private CurrentCode currentCode;
        private CodeChangeZone changeZone;

        private enum StopZone
        {
            NotApplicable,
            InApproach,
            Restricting
        }
        private StopZone stopZone;

        private PulseCode displayCode;
        private PulseCode DisplayCode
        {
            get
            {
                return displayCode;
            }
            set
            {
                if (displayCode == value)
                    return;

                Message(ConfirmLevel.None, "Cab Signal: " + PulseCodeMapping.ToMessageString(value));
                if (value < displayCode)
                    Alarm = AlarmState.Countdown;
                displayCode = value;
            }
        }

        private enum AlarmState
        {
            Off,
            Countdown
        }
        private AlarmState alarm;
        private Timer alarmTimer;
        private AlarmState Alarm
        {
            get
            {
                return alarm;
            }
            set
            {
                if (alarm == AlarmState.Off && value == AlarmState.Countdown)
                {
                    alarmTimer.Setup(CountdownSec);
                    alarmTimer.Start();
                    TriggerSoundAlert1();
                }
                else if (alarm == AlarmState.Countdown && value == AlarmState.Off)
                {
                    TriggerSoundAlert2();
                }

                alarm = value;
            }
        }

        public override void Initialize()
        {
            stopZone = StopZone.NotApplicable;
            blockTracker = new BlockTracker(this, HandleBlockChange);
            currentCode = new CurrentCode(this, PulseCode.Restricting); // TODO - spawn with Clear at speed?
            changeZone = new CodeChangeZone(this);

            alarm = AlarmState.Off;
            alarmTimer = new Timer(this);

            Console.WriteLine("CSS initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            if (evt == TCSEvent.AlerterPressed)
                if (Alarm == AlarmState.Countdown)
                    Alarm = AlarmState.Off;
        }

        private void HandleBlockChange()
        {
            currentCode.HandleBlockChange();
            changeZone.HandleBlockChange();

            // If passing another Approach signal, allow the displayed aspect to move back to Approach.
            PulseCode code = currentCode.GetCurrent();
            if (code == PulseCode.Approach)
                stopZone = StopZone.InApproach;
        }

        public override void SetEmergency(bool emergency)
        {
            // TODO
        }

        public override void Update()
        {
            if (!IsTrainControlEnabled())
                return;

            blockTracker.Update();

            PulseCode code = currentCode.GetCurrent();
            if (code == PulseCode.Approach && changeZone.Inside())
                stopZone = StopZone.Restricting;
            // Once in Restricting, the displayed aspect should stay in Restricting.
            else if (code == PulseCode.Approach && stopZone != StopZone.Restricting)
                stopZone = StopZone.InApproach;
            else
                stopZone = StopZone.NotApplicable;

            if (code == PulseCode.Restricting || stopZone == StopZone.Restricting)
            {
                DisplayCode = PulseCode.Restricting;
            }
            else
            {
                Aspect nextAspect;
                try
                {
                    nextAspect = NextSignalAspect(0);
                }
                catch (NullReferenceException)
                {
                    nextAspect = Aspect.None;
                }
                var nextCode = PulseCodeMapping.ToPulseCode(nextAspect);
                DisplayCode = nextCode > code ? nextCode : code;
            }
            SetNextSignalAspect(PulseCodeMapping.ToCabDisplay(DisplayCode));

            // TODO
            if (Alarm == AlarmState.Countdown && alarmTimer.Triggered)
                Alarm = AlarmState.Off;
        }
    }
}

internal class BlockTracker
{
    private readonly TrainControlSystem tcs;
    private readonly Action nextBlock;

    private enum SignalPosition
    {
        Far,
        Near
    }
    private SignalPosition signal = SignalPosition.Far;
    private SignalPosition Signal
    {
        get
        {
            return signal;
        }
        set
        {
            if (signal == SignalPosition.Far && value == SignalPosition.Near)
                nextBlock();

            signal = value;
        }
    }

    public BlockTracker(TrainControlSystem parent, Action callback)
    {
        tcs = parent;
        nextBlock = callback;
    }

    public void Update()
    {
        float distanceM;
        try
        {
            distanceM = tcs.NextSignalDistanceM(0);
        }
        catch (NullReferenceException)
        {
            Signal = SignalPosition.Far;
            return;
        }
        Signal = distanceM < 3f ? SignalPosition.Near : SignalPosition.Far;
    }
}

/*
 *                                signal  o
 *                                        |
 * +--------------------------------------+
 *     signal block
 *                     +------------------+
 *                       code change zone
 */
internal class CodeChangeZone
{
    private readonly TrainControlSystem tcs;
    private float blockLengthM = 0f;

    public CodeChangeZone(TrainControlSystem parent)
    {
        tcs = parent;
    }

    public bool Inside()
    {
        float nextDistanceM;
        try
        {
            nextDistanceM = tcs.NextSignalDistanceM(0);
        }
        catch (NullReferenceException)
        {
            return false;
        }
        const float ft2m = 0.3048f;
        return nextDistanceM < Math.Max(blockLengthM / 2, 1500 * ft2m);
    }

    public void Update()
    {
        float distanceM;
        try
        {
            distanceM = tcs.NextSignalDistanceM(0);
        }
        catch (NullReferenceException)
        {
            return;
        }
    }

    public void HandleBlockChange()
    {
        float lengthM = tcs.NextSignalDistanceM(0);
        blockLengthM = lengthM;
    }
}

// Order matters. Later codes are upgrades (>) over earlier ones.
internal enum PulseCode
{
    Restricting,
    Approach,
    ApproachMedium,
    Clear
}

internal static class PulseCodeMapping
{
    public static PulseCode ToPulseCode(Aspect aspect)
    {
        switch (aspect)
        {
            case Aspect.Clear_2:
            case Aspect.Clear_1:
                return PulseCode.Clear;
            case Aspect.Approach_3:
            case Aspect.Approach_2:
                return PulseCode.ApproachMedium;
            case Aspect.Approach_1:
                return PulseCode.Approach;
            case Aspect.Restricted:
            case Aspect.StopAndProceed:
            case Aspect.Stop:
            case Aspect.Permission:
            default:
                return PulseCode.Restricting;
        }
    }

    public static float ToSpeedMpS(PulseCode code)
    {
        const float mph2mps = 0.44704f;
        switch (code)
        {
            case PulseCode.Clear:
                return 0f; // no restriction
            case PulseCode.ApproachMedium:
                return 45 * mph2mps;
            case PulseCode.Approach:
                return 30 * mph2mps;
            case PulseCode.Restricting:
                return 20 * mph2mps;
            default:
                return 0f;
        }
    }

    public static string ToMessageString(PulseCode code)
    {
        switch (code)
        {
            case PulseCode.Clear:
                return "Clear";
            case PulseCode.ApproachMedium:
                return "Approach Medium - 45 mph";
            case PulseCode.Approach:
                return "Approach - 30 mph";
            case PulseCode.Restricting:
                return "Restricting - 20 mph";
            default:
                return null;
        }
    }

    public static Aspect ToCabDisplay(PulseCode code)
    {
        switch (code)
        {
            case PulseCode.Clear:
                return Aspect.Clear_2;
            case PulseCode.ApproachMedium:
                return Aspect.Approach_3;
            case PulseCode.Approach:
                return Aspect.Approach_1;
            case PulseCode.Restricting:
                return Aspect.Stop;
            default:
                return Aspect.None;
        }
    }
}

internal class CurrentCode
{
    private readonly TrainControlSystem tcs;
    private PulseCode code;

    public CurrentCode(TrainControlSystem parent, PulseCode initCode)
    {
        tcs = parent;
        code = initCode;
    }

    public PulseCode GetCurrent()
    {
        return code;
    }

    public void HandleBlockChange()
    {
        PulseCode newCode = PulseCodeMapping.ToPulseCode(tcs.NextSignalAspect(0));
        Console.WriteLine("CSS: {0} -> {1}", code, newCode);
        code = newCode;
    }
}
