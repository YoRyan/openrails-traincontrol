/*
 * Amtrak Automatic Train Control/
 * Advanced Civil Speed Enforcement System for Open Rails
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
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_AMTK_ATC_ACSES : TrainControlSystem
    {
        public const float CountdownS = 6f;
        public const float UpgradeSoundS = 0.55f;
        public const float SpeedLimitMarginMpS = 1.34f; // 3 mph
        public const float MinStopZoneLengthM = 457f; // 1500 ft

        // According to the Train Sim World: Northeast Corridor New York manual, these rates should be:
        //public const float SuppressingAccelMpSS = -0.5f;
        //public const float SuppressionAccelMpSS = -1.5f;
        // But these seem unachievable in Open Rails.
        public const float SuppressingAccelMpSS = -0.25f;
        public const float SuppressionAccelMpSS = -0.5f;

        private float blockLengthM = TrainControlSystemExtensions.NullSignalDistance;

        private BlockTracker blockTracker;
        private PenaltyBrake penaltyBrake;
        private Alerter alerter;
        private Aspect blockAspect;
        private CodeChangeZone changeZone;
        private Sound upgradeSound;
        private ISubsystem[] subsystems;

        private PulseCode displayCode;
        private PulseCode DisplayCode
        {
            get
            {
                return displayCode;
            }
            set
            {
                if (value < displayCode)
                {
                    float speed = PulseCodeMapping.ToSpeedMpS(value);
                    bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;
                    Atc = overspeed ? ATCState.OverspeedCountdown : ATCState.Countdown;
                }
                else if (value > displayCode)
                {
                    upgradeSound.Play();
                }
                displayCode = value;
            }
        }

        private enum ATCState
        {
            Off,
            Countdown,          // Acknowledge the alarm
            OverspeedCountdown, // Acknowledge the alarm (w/ penalty display)
            Overspeed,          // Start slowing the train
            OverspeedSlowing,   // Reached -0.5 m/s^2
            OverspeedSuppress,  // Reached -1.5 m/s^2
            Stop                // Penalty
        }
        private ATCState atc;
        private Timer atcTimer;
        private ATCState Atc
        {
            get
            {
                return atc;
            }
            set
            {
                if (atc == ATCState.Off)
                {
                    switch (value)
                    {
                        case ATCState.Countdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.OverspeedCountdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.Overspeed:
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.Stop:
                            penaltyBrake.Set();
                            break;
                    }
                }
                else if (atc == ATCState.Countdown)
                {
                    switch (value)
                    {
                        case ATCState.Off:
                        case ATCState.OverspeedSuppress:
                            TriggerSoundWarning2();
                            break;
                        case ATCState.OverspeedCountdown:
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.Overspeed:
                            TriggerSoundWarning2();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning2();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.Stop:
                            TriggerSoundWarning2();
                            penaltyBrake.Set();
                            break;
                    }
                }
                else if (atc == ATCState.OverspeedCountdown)
                {
                    switch (value)
                    {
                        case ATCState.Off:
                            TriggerSoundWarning2();
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Countdown:
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Overspeed:
                            TriggerSoundWarning2();
                            break;
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning2();
                            break;
                        case ATCState.OverspeedSuppress:
                            TriggerSoundWarning2();
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Stop:
                            penaltyBrake.Set();
                            TriggerSoundWarning2();
                            SetOverspeedWarningDisplay(false);
                            break;
                    }
                }
                else if (atc == ATCState.Overspeed)
                {
                    switch (value)
                    {
                        case ATCState.Off:
                        case ATCState.OverspeedSuppress:
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Countdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.OverspeedCountdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            break;
                        case ATCState.Stop:
                            penaltyBrake.Set();
                            SetOverspeedWarningDisplay(false);
                            break;
                    }
                }
                else if (atc == ATCState.OverspeedSlowing)
                {
                    switch (value)
                    {
                        case ATCState.Off:
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Countdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.OverspeedCountdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.Overspeed:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            break;
                        case ATCState.OverspeedSuppress:
                            SetOverspeedWarningDisplay(false);
                            break;
                        case ATCState.Stop:
                            penaltyBrake.Set();
                            SetOverspeedWarningDisplay(false);
                            break;
                    }
                }
                else if (atc == ATCState.OverspeedSuppress)
                {
                    switch (value)
                    {
                        case ATCState.Countdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.OverspeedCountdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.Overspeed:
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            break;
                        case ATCState.Stop:
                            penaltyBrake.Set();
                            break;
                    }
                }
                else if (atc == ATCState.Stop)
                {
                    switch (value)
                    {
                        case ATCState.Off:
                            penaltyBrake.Release();
                            break;
                        case ATCState.Countdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            penaltyBrake.Release();
                            TriggerSoundWarning1();
                            break;
                        case ATCState.OverspeedCountdown:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            penaltyBrake.Release();
                            TriggerSoundWarning1();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.Overspeed:
                        case ATCState.OverspeedSlowing:
                            atcTimer.Setup(CountdownS);
                            atcTimer.Start();
                            penaltyBrake.Release();
                            SetOverspeedWarningDisplay(true);
                            break;
                        case ATCState.OverspeedSuppress:
                            penaltyBrake.Release();
                            break;
                    }
                }

                if (atc != value)
                {
                    Console.WriteLine(string.Format("ATC Alarm: {0} -> {1}", atc, value));
                    atc = value;
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
                GetFloatParameter("Alerter", "AcknowledgeTimeS", CountdownS),
                GetBoolParameter("Alerter", "DoControlsReset", true),
                penaltyBrake.Set, penaltyBrake.Release);
            changeZone = new CodeChangeZone(this, blockTracker);
            upgradeSound = new Sound(this, TriggerSoundAlert1, TriggerSoundAlert2, UpgradeSoundS);
            subsystems = new ISubsystem[] { blockTracker, alerter, upgradeSound };

            blockAspect = Aspect.Clear_2;
            displayCode = PulseCodeMapping.ToPulseCode(blockAspect);
            atc = ATCState.Off;
            atcTimer = new Timer(this);

            Console.WriteLine("ATC initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            foreach (ISubsystem sub in subsystems)
                sub.HandleEvent(evt, message);

            if (evt == TCSEvent.AlerterPressed)
            {
                if (Atc == ATCState.Countdown)
                    Atc = ATCState.Off;
                else if (Atc == ATCState.OverspeedCountdown)
                    Atc = ATCState.Overspeed;
                else if (Atc == ATCState.Stop && this.IsStopped())
                    Atc = ATCState.Off;
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
            if (GameTime() >= 1f)
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
            }

            SetNextSignalAspect(PulseCodeMapping.ToCabDisplay(DisplayCode));
            SetNextSpeedLimitMpS(PulseCodeMapping.ToSpeedMpS(DisplayCode));
        }

        private void UpdateAlarm()
        {
            if (!IsTrainControlEnabled())
            {
                if ((Atc == ATCState.Countdown || Atc == ATCState.OverspeedCountdown) && atcTimer.Triggered)
                    Atc = ATCState.Off;
                else if (Atc == ATCState.Overspeed)
                    Atc = ATCState.Off;
                return;
            }

            float accelMpSS = Locomotive().AccelerationMpSS;
            float speed = PulseCodeMapping.ToSpeedMpS(DisplayCode);
            bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;

            if (Atc == ATCState.Off && overspeed)
            {
                Atc = ATCState.OverspeedCountdown;
            }
            else if ((Atc == ATCState.Countdown || Atc == ATCState.OverspeedCountdown) && atcTimer.Triggered)
            {
                Atc = ATCState.Stop;
            }
            else if (Atc == ATCState.Overspeed)
            {
                if (!overspeed)
                    Atc = ATCState.Off;
                else if (accelMpSS <= SuppressingAccelMpSS)
                    Atc = ATCState.OverspeedSlowing;
                else if (atcTimer.Triggered)
                    Atc = ATCState.Stop;
            }
            else if (Atc == ATCState.OverspeedSlowing)
            {
                if (!overspeed)
                    Atc = ATCState.Off;
                else if (accelMpSS <= SuppressionAccelMpSS)
                    Atc = ATCState.OverspeedSuppress;
                else if (accelMpSS > SuppressingAccelMpSS)
                    Atc = ATCState.OverspeedCountdown;
                else if (atcTimer.Triggered)
                    Atc = ATCState.Stop;
            }
            else if (Atc == ATCState.OverspeedSuppress)
            {
                if (!overspeed)
                    Atc = ATCState.Off;
                else if (accelMpSS > SuppressionAccelMpSS)
                    Atc = ATCState.OverspeedCountdown;
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

    public void HandleEvent(TCSEvent evt, string message) {}

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
    CabSpeed60,
    CabSpeed80,
    Clear100,
    Clear125,
    Clear150
}

internal static class PulseCodeMapping
{
    public static PulseCode ToPulseCode(Aspect aspect)
    {
        switch (aspect)
        {
            case Aspect.Clear_2:
                return PulseCode.Clear125;
            case Aspect.Clear_1:
            case Aspect.Approach_2:
                return PulseCode.ApproachMedium;
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
                return PulseCode.Clear125;
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
            case PulseCode.Clear150:
                return 150 * mph2mps;
            case PulseCode.Clear125:
                return 125 * mph2mps;
            case PulseCode.Clear100:
                return 100 * mph2mps;
            case PulseCode.CabSpeed80:
                return 80 * mph2mps;
            case PulseCode.CabSpeed60:
                return 60 * mph2mps;
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
            case PulseCode.Clear150:
                return "Clear - 150 mph";
            case PulseCode.Clear125:
                return "Clear - 125 mph";
            case PulseCode.Clear100:
                return "Clear - 100 mph";
            case PulseCode.CabSpeed80:
                return "Cab Speed - 80 mph";
            case PulseCode.CabSpeed60:
                return "Cab Speed - 60 mph";
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
            case PulseCode.Clear150:
            case PulseCode.Clear125:
            case PulseCode.Clear100:
                return Aspect.Clear_2;
            case PulseCode.CabSpeed80:
            case PulseCode.CabSpeed60:
                return Aspect.Clear_1;
            case PulseCode.ApproachMedium:
                return Aspect.Approach_3;
            case PulseCode.Approach:
                return Aspect.Approach_1;
            case PulseCode.Restricting:
                return Aspect.Restricted;
            default:
                return Aspect.None;
        }
    }
}

internal abstract class SharedLatch
{
    private uint users = 0;

    public void Set()
    {
        if (users++ == 0)
            DoSet();
    }

    public void Release()
    {
        if (--users == 0)
            DoRelease();
    }

    protected abstract void DoSet();
    protected abstract void DoRelease();
}

internal class PenaltyBrake : SharedLatch
{
    private readonly TrainControlSystem tcs;

    public PenaltyBrake(TrainControlSystem parent)
    {
        tcs = parent;
    }

    protected override void DoSet()
    {
        tcs.SetFullBrake(true);
        tcs.SetPenaltyApplicationDisplay(true);
    }

    protected override void DoRelease()
    {
        tcs.SetFullBrake(false);
        tcs.SetPenaltyApplicationDisplay(false);
    }
}

internal class Sound : ISubsystem
{
    private readonly Timer timer;
    private readonly float durationS;
    private readonly Action start;
    private readonly Action stop;

    public Sound(TrainControlSystem tcs, Action startPlaying, Action stopPlaying, float durationS)
    {
        timer = new Timer(tcs);
        start = startPlaying;
        stop = stopPlaying;
        this.durationS = durationS;
    }

    public void HandleEvent(TCSEvent evt, string message) { }

    public void Update()
    {
        if (timer.Triggered)
        {
            timer.Stop();
            stop();
        }
    }

    public void Play()
    {
        if (!timer.Started)
        {
            timer.Setup(durationS);
            timer.Start();
            start();
        }
    }
}
