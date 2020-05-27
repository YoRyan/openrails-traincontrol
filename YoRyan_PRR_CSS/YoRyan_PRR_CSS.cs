/*
 * PRR Cab Signaling System for Open Rails
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
        public const float UpgradeSoundSec = 1f;
        public const float SpeedLimitMarginMpS = 1.34112f; // 3 mph
        public const float MinStopZoneLengthM = 457f; // 1500 ft

        private bool hasSpeedControl;
        private float blockLengthM = TrainControlSystemExtensions.NullSignalDistance;

        private BlockTracker blockTracker;
        private PenaltyBrake penaltyBrake;
        private Alerter alerter;
        private ISubsystem[] subsystems;
        private Aspect blockAspect;
        private CodeChangeZone changeZone;

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
                {
                    if (Alarm == AlarmState.Off)
                        Alarm = AlarmState.Countdown;
                }
                displayCode = value;
            }
        }

        private enum AlarmState
        {
            Off,
            Countdown,
            Overspeed,
            OverspeedSuppress,
            Stop
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
                Action overspeedWarning = () =>
                {
                    Message(ConfirmLevel.None, "Cab Signal: Overspeed! Slow your train immediately.");
                };
                Action penaltyWarning = () =>
                {
                    Message(ConfirmLevel.None, "Cab Signal: Penalty brake application.");
                };

                if (alarm == AlarmState.Off)
                {
                    if (value == AlarmState.Countdown)
                    {
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        TriggerSoundWarning1();
                    }
                    else if (value == AlarmState.Overspeed)
                    {
                        SetOverspeedWarningDisplay(true);
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        overspeedWarning();
                    }
                    else if (value == AlarmState.OverspeedSuppress)
                    {
                        SetOverspeedWarningDisplay(true);
                    }
                    else if (value == AlarmState.Stop)
                    {
                        penaltyBrake.Set();
                        penaltyWarning();
                    }
                }
                else if (alarm == AlarmState.Countdown)
                {
                    if (value == AlarmState.Off)
                    {
                        TriggerSoundWarning2();
                    }
                    else if (value == AlarmState.Overspeed)
                    {
                        TriggerSoundWarning2();
                        SetOverspeedWarningDisplay(true);
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        overspeedWarning();
                    }
                    else if (value == AlarmState.OverspeedSuppress)
                    {
                        TriggerSoundWarning2();
                        SetOverspeedWarningDisplay(true);
                        overspeedWarning();
                    }
                    else if (value == AlarmState.Stop)
                    {
                        TriggerSoundWarning2();
                        penaltyBrake.Set();
                        penaltyWarning();
                    }
                }
                else if (alarm == AlarmState.Overspeed)
                {
                    if (value == AlarmState.Off || value == AlarmState.Countdown)
                    {
                        SetOverspeedWarningDisplay(false);
                    }
                    else if (value == AlarmState.Stop)
                    {
                        SetOverspeedWarningDisplay(false);
                        penaltyBrake.Set();
                        penaltyWarning();
                    }
                }
                else if (alarm == AlarmState.OverspeedSuppress)
                {
                    if (value == AlarmState.Off || value == AlarmState.Countdown)
                    {
                        SetOverspeedWarningDisplay(false);
                    }
                    else if (value == AlarmState.Overspeed)
                    {
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        overspeedWarning();
                    }
                    else if (value == AlarmState.Stop)
                    {
                        SetOverspeedWarningDisplay(false);
                        penaltyBrake.Set();
                        penaltyWarning();
                    }
                }
                else if (alarm == AlarmState.Stop)
                {
                    if (alarm == AlarmState.Countdown)
                    {
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        penaltyBrake.Release();
                        TriggerSoundWarning1();
                    }
                    else if (alarm == AlarmState.Off)
                    {
                        penaltyBrake.Release();
                    }
                    else if (alarm == AlarmState.Overspeed)
                    {
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        penaltyBrake.Release();
                        SetOverspeedWarningDisplay(true);
                        overspeedWarning();
                    }
                    else if (alarm == AlarmState.OverspeedSuppress)
                    {
                        penaltyBrake.Release();
                        SetOverspeedWarningDisplay(true);
                        overspeedWarning();
                    }
                }

                if (alarm != value)
                {
                    Console.WriteLine(string.Format("CSS Alarm: {0} -> {1}", alarm, value));
                    alarm = value;
                }
            }
        }

        public override void Initialize()
        {
            blockTracker = new BlockTracker(this);
            blockTracker.NewSignalBlock += HandleNewSignalBlock;
            penaltyBrake = new PenaltyBrake(this);
            alerter = new Alerter(
                this,
                GetFloatParameter("Alerter", "CountdownTimeS", 60f),
                GetFloatParameter("Alerter", "AcknowledgeTimeS", CountdownSec),
                GetBoolParameter("Alerter", "DoControlsReset", false),
                penaltyBrake.Set, penaltyBrake.Release);
            changeZone = new CodeChangeZone(this, blockTracker);
            subsystems = new ISubsystem[] { blockTracker, alerter };

            alarm = AlarmState.Off;
            alarmTimer = new Timer(this);
            blockAspect = Aspect.Clear_2;
            displayCode = PulseCodeMapping.ToPulseCode(blockAspect);
            hasSpeedControl = GetBoolParameter("CSS", "SpeedControl", true);

            Console.WriteLine("CSS initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            foreach (ISubsystem sub in subsystems)
                sub.HandleEvent(evt, message);

            if (evt == TCSEvent.AlerterPressed)
            {
                if (Alarm == AlarmState.Countdown)
                {
                    float speed = PulseCodeMapping.ToSpeedMpS(DisplayCode);
                    bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;
                    Alarm = overspeed ? AlarmState.Overspeed : AlarmState.Off;
                }
                else if (Alarm == AlarmState.Stop && this.IsStopped())
                {
                    Alarm = AlarmState.Off;
                }
            }
        }

        private void HandleNewSignalBlock(object _, SignalBlockEventArgs e)
        {
            blockAspect = e.Aspect;
            blockLengthM = e.BlockLengthM;

            // Move the cab signal out of Restricting.
            if (DisplayCode == PulseCode.Restricting)
                DisplayCode = PulseCodeMapping.ToPulseCode(blockAspect);
        }

        public override void SetEmergency(bool emergency)
        {
            SetEmergencyBrake(emergency);
        }

        public override void Update()
        {
            foreach (ISubsystem sub in subsystems)
                sub.Update();

            if (blockLengthM == TrainControlSystemExtensions.NullSignalDistance)
                blockLengthM = this.SafeNextSignalDistanceM(0);

            UpdateCode();
            UpdateAlarm();
        }

        private void UpdateCode()
        {
            float nextSignalM = this.SafeNextSignalDistanceM(0);
            PulseCode changeCode = PulseCodeMapping.ToPriorPulseCode(this.SafeNextSignalAspect(0));
            if (nextSignalM != TrainControlSystemExtensions.NullSignalDistance && nextSignalM <= MinStopZoneLengthM && changeCode == PulseCode.Restricting)
                DisplayCode = PulseCode.Restricting;
            else if (DisplayCode == PulseCode.Restricting)
                DisplayCode = PulseCode.Restricting;
            else if (changeZone.Inside())
                DisplayCode = changeCode;
            else
                DisplayCode = PulseCodeMapping.ToPulseCode(blockAspect);

            SetNextSignalAspect(PulseCodeMapping.ToCabDisplay(DisplayCode));
            SetNextSpeedLimitMpS(PulseCodeMapping.ToSpeedMpS(DisplayCode));
        }

        private void UpdateAlarm()
        {
            if (!IsTrainControlEnabled())
            {
                if (Alarm != AlarmState.Countdown || alarmTimer.Triggered)
                    Alarm = AlarmState.Off;
                return;
            }

            ControllerState brake = Locomotive().TrainBrakeController.TrainBrakeControllerState;
            bool suppressing = brake == ControllerState.Suppression || brake == ControllerState.ContServ || brake == ControllerState.FullServ;
            float speed = PulseCodeMapping.ToSpeedMpS(DisplayCode);
            bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;

            if (Alarm == AlarmState.Off && overspeed && hasSpeedControl)
            {
                Alarm = AlarmState.Overspeed;
            }
            else if (Alarm == AlarmState.Countdown && alarmTimer.Triggered)
            {
                Alarm = AlarmState.Stop;
            }
            else if (Alarm == AlarmState.Overspeed)
            {
                if (!overspeed)
                    Alarm = AlarmState.Off;
                else if (suppressing)
                    Alarm = AlarmState.OverspeedSuppress;
                else if (alarmTimer.Triggered)
                    Alarm = AlarmState.Stop;
            }
            else if (Alarm == AlarmState.OverspeedSuppress)
            {
                if (!overspeed)
                    Alarm = AlarmState.Off;
                else if (!suppressing)
                    Alarm = AlarmState.Overspeed;
            }
        }
    }
}

internal interface ISubsystem
{
    void HandleEvent(TCSEvent evt, string message);
    void Update();
}

internal static class TrainControlSystemExtensions
{
    public const float NullSignalDistance = 0f;
    public const Aspect NullSignalAspect = Aspect.None;

    public static float SafeNextSignalDistanceM(this TrainControlSystem tcs, int foresight)
    {
        float distanceM;
        try
        {
            distanceM = tcs.NextSignalDistanceM(foresight);
        }
        catch (NullReferenceException)
        {
            return NullSignalDistance;
        }
        return distanceM;
    }

    public static Aspect SafeNextSignalAspect(this TrainControlSystem tcs, int foresight)
    {
        Aspect aspect;
        try
        {
            aspect = tcs.NextSignalAspect(foresight);
        }
        catch (NullReferenceException)
        {
            return NullSignalAspect;
        }
        return aspect;
    }

    public static bool IsStopped(this TrainControlSystem tcs)
    {
        return tcs.SpeedMpS() < 0.1f;
    }
}

internal class Alerter : ISubsystem
{
    private readonly TrainControlSystem tcs;
    private readonly Action setBrake;
    private readonly Action releaseBrake;
    private readonly float acknowledgeTimeS;
    private readonly bool doControlsReset;
    private readonly Vigilance vigilance;
    private readonly Timer timer;

    private enum AlerterState
    {
        Countdown,
        Alarm,
        Stop
    }
    private AlerterState state = AlerterState.Countdown;
    private AlerterState State
    {
        get
        {
            return state;
        }
        set
        {
            if (state == AlerterState.Countdown)
            {
                if (value == AlerterState.Alarm)
                {
                    tcs.SetVigilanceAlarm(true);
                    tcs.SetVigilanceAlarmDisplay(true);
                    timer.Setup(acknowledgeTimeS);
                    timer.Start();
                }
                else if (value == AlerterState.Stop)
                {
                    tcs.SetVigilanceAlarm(true);
                    tcs.SetVigilanceAlarmDisplay(true);

                    setBrake();
                }
            }
            else if (state == AlerterState.Alarm)
            {
                if (value == AlerterState.Countdown)
                {
                    tcs.SetVigilanceAlarm(false);
                    tcs.SetVigilanceAlarmDisplay(false);
                }
                else if (value == AlerterState.Stop)
                {
                    setBrake();
                }
            }
            else if (state == AlerterState.Stop)
            {
                if (value == AlerterState.Countdown)
                {
                    tcs.SetVigilanceAlarm(false);
                    tcs.SetVigilanceAlarmDisplay(false);

                    releaseBrake();
                }
                else if (value == AlerterState.Alarm)
                {
                    tcs.SetVigilanceAlarm(false);
                    tcs.SetVigilanceAlarmDisplay(false);
                    timer.Setup(acknowledgeTimeS);
                    timer.Start();

                    releaseBrake();
                }
            }

            state = value;
        }
    }

    public Alerter(TrainControlSystem parent, float countdownTimeS, float acknowledgeTimeS, bool doControlsReset, Action setBrake, Action releaseBrake)
    {
        tcs = parent;
        this.setBrake = setBrake;
        this.releaseBrake = releaseBrake;
        this.acknowledgeTimeS = acknowledgeTimeS;
        this.doControlsReset = doControlsReset;
        vigilance = new Vigilance(tcs, countdownTimeS);
        vigilance.Trip += HandleVigilanceTrip;
        timer = new Timer(tcs);
    }

    private void HandleVigilanceTrip(object sender, EventArgs _)
    {
        if (State == AlerterState.Countdown)
            State = AlerterState.Alarm;
    }

    public void HandleEvent(TCSEvent evt, string message)
    {
        vigilance.HandleEvent(evt, message);

        if (evt == TCSEvent.AlerterPressed)
        {
            Reset();
        }
        else if (doControlsReset)
        {
            switch (evt)
            {
                case TCSEvent.AlerterPressed:
                case TCSEvent.ThrottleChanged:
                case TCSEvent.TrainBrakeChanged:
                case TCSEvent.EngineBrakeChanged:
                case TCSEvent.DynamicBrakeChanged:
                case TCSEvent.ReverserChanged:
                case TCSEvent.GearBoxChanged:
                    Reset();
                    break;
            }
        }
    }

    public void Update()
    {
        vigilance.Update();

        if (tcs.IsTrainControlEnabled())
        {
            if (State == AlerterState.Alarm && timer.Triggered)
                State = AlerterState.Stop;
        }
        else
        {
            State = AlerterState.Countdown;
        }
    }

    private void Reset()
    {
        vigilance.Reset();

        if (State == AlerterState.Alarm)
            State = AlerterState.Countdown;
        else if (State == AlerterState.Stop && tcs.IsStopped())
            State = AlerterState.Countdown;
    }
}

internal class Vigilance : ISubsystem
{
    public event EventHandler Trip;

    private readonly TrainControlSystem tcs;
    private readonly Timer timer;
    private readonly float countdownTimeS;

    public Vigilance(TrainControlSystem parent, float countdownTimeS)
    {
        tcs = parent;
        timer = new Timer(tcs);
        this.countdownTimeS = countdownTimeS;
    }

    public void HandleEvent(TCSEvent evt, string message) { }

    public void Update()
    {
        if (!tcs.IsTrainControlEnabled())
        {
            timer.Stop();
            return;
        }

        if (timer.Triggered)
        {
            timer.Stop();
            Trip.Invoke(this, EventArgs.Empty);
        }
        else if (countdownTimeS > 0 && tcs.IsAlerterEnabled() && !tcs.IsStopped())
        {
            if (!timer.Started)
            {
                timer.Setup(countdownTimeS);
                timer.Start();
            }
        }
        else
        {
            Reset();
        }
    }

    public void Reset()
    {
        timer.Stop();
    }
}

internal class SignalBlockEventArgs : EventArgs
{
    public readonly Aspect Aspect;
    public readonly float BlockLengthM;

    public SignalBlockEventArgs(Aspect aspect, float blockLengthM)
    {
        Aspect = aspect;
        BlockLengthM = blockLengthM;
    }
}

internal class BlockTracker : ISubsystem
{
    public event EventHandler<SignalBlockEventArgs> NewSignalBlock;
    private readonly TrainControlSystem tcs;

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
            {
                var e = new SignalBlockEventArgs(tcs.SafeNextSignalAspect(0), tcs.SafeNextSignalDistanceM(1));
                NewSignalBlock.Invoke(this, e);
            }

            signal = value;
        }
    }

    public BlockTracker(TrainControlSystem parent)
    {
        tcs = parent;
    }

    public void HandleEvent(TCSEvent evt, string message) { }

    public void Update()
    {
        float distanceM = tcs.SafeNextSignalDistanceM(0);
        Signal = distanceM != TrainControlSystemExtensions.NullSignalDistance && distanceM < 3f ? SignalPosition.Near : SignalPosition.Far;
    }
}

/*
 *                                signal  o
 *                                        |
 * +--------------------------------------+
 *     signal block    +------------------+
 *                       code change zone
 */
internal class CodeChangeZone
{
    private readonly TrainControlSystem tcs;
    private float blockLengthM = 0f;
    private float codeChangeM = 0f;
    private float trailingSwitchM = 0f;

    public CodeChangeZone(TrainControlSystem parent, BlockTracker blockTracker)
    {
        tcs = parent;
        blockTracker.NewSignalBlock += HandleNewSignalBlock;
    }

    public bool Inside()
    {
        float nextDistanceM = tcs.SafeNextSignalDistanceM(0);
        if (nextDistanceM == TrainControlSystemExtensions.NullSignalDistance)
            return false;
        else if (trailingSwitchM != float.MaxValue)
            return nextDistanceM < Math.Max(blockLengthM - codeChangeM, blockLengthM - trailingSwitchM);
        else
            return nextDistanceM < blockLengthM - codeChangeM;
    }

    public void HandleNewSignalBlock(object _, SignalBlockEventArgs e)
    {
        blockLengthM = e.BlockLengthM;
        codeChangeM = e.BlockLengthM / 2;
        trailingSwitchM = tcs.NextTrailingDivergingSwitchDistanceM(e.BlockLengthM);
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
                return PulseCode.Clear;
            case Aspect.Approach_2:
                return PulseCode.ApproachMedium;
            case Aspect.Clear_1:
            case Aspect.Approach_3:
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

    public static PulseCode ToPriorPulseCode(Aspect aspect)
    {
        switch (aspect)
        {
            case Aspect.Clear_2:
            case Aspect.Approach_2:
                return PulseCode.Clear;
            case Aspect.Clear_1:
            case Aspect.Approach_3:
            case Aspect.Approach_1:
                return PulseCode.ApproachMedium;
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

internal class PenaltyBrake
{
    private readonly TrainControlSystem tcs;
    private uint applications = 0;

    public PenaltyBrake(TrainControlSystem parent)
    {
        tcs = parent;
    }

    public void Set()
    {
        if (applications++ == 0)
        {
            tcs.SetFullBrake(true);
            tcs.SetPenaltyApplicationDisplay(true);
        }
    }

    public void Release()
    {
        if (--applications == 0)
        {
            tcs.SetFullBrake(false);
            tcs.SetPenaltyApplicationDisplay(false);
        }
    }
}
