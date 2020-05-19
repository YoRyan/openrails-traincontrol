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
using System.Collections.Generic;
using Orts.Simulation;
using Orts.Simulation.RollingStocks;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_AMTK_ATC_ACSES : TrainControlSystem
    {
        public const float CountdownS = 6f;
        public const float UpgradeSoundS = 0.55f;
        public const float SpeedLimitMarginMpS = 1.34f; // 3 mph
        public const float MinStopZoneLengthM = 457f; // 1500 ft

        // Taken from the Train Sim World: Northeast Corridor New York manual.
        public const float SuppressingAccelMpSS = -0.5f;
        //public const float SuppressionAccelMpSS = -1.5f; // This rate seems unachievable in Open Rails.
        public const float SuppressionAccelMpSS = -1.0f;

        private float blockLengthM = TCSUtils.NullSignalDistance;

        private BlockTracker blockTracker;
        private PenaltyBrake penaltyBrake;
        private Vigilance vigilance;
        private CurrentCode currentCode;
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
                    float speed = PulseCodeMapping.ToSpeedMpS(value);
                    bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;
                    if (Atc == ATCState.Off && IsTrainControlEnabled())
                        Atc = overspeed ? ATCState.OverspeedCountdown : ATCState.Countdown;
                }
                else
                {
                    Upgrade = UpgradeState.Play;
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

        private enum UpgradeState
        {
            Off,
            Play
        }
        private UpgradeState upgrade;
        private Timer upgradeTimer;
        private UpgradeState Upgrade
        {
            get
            {
                return upgrade;
            }
            set
            {
                if (upgrade == UpgradeState.Off && value == UpgradeState.Play)
                {
                    upgradeTimer.Setup(UpgradeSoundS);
                    upgradeTimer.Start();
                    TriggerSoundAlert1();
                }
                else if (upgrade == UpgradeState.Play && value == UpgradeState.Off)
                {
                    TriggerSoundAlert2();
                }

                upgrade = value;
            }
        }

        private enum AlerterState
        {
            Countdown,
            Alert,
            Stop
        }
        private AlerterState alerter;
        private Timer alerterTimer;
        private AlerterState Alerter
        {
            get
            {
                return alerter;
            }
            set
            {
                if (alerter == AlerterState.Countdown)
                {
                    if (value == AlerterState.Alert)
                    {
                        SetVigilanceAlarm(true);
                        SetVigilanceAlarmDisplay(true);
                        alerterTimer.Setup(CountdownS);
                        alerterTimer.Start();
                    }
                    else if (value == AlerterState.Stop)
                    {
                        SetVigilanceAlarm(true);
                        SetVigilanceAlarmDisplay(true);
                        penaltyBrake.Set();
                    }
                }
                else if (alerter == AlerterState.Alert)
                {
                    if (value == AlerterState.Countdown)
                    {
                        SetVigilanceAlarm(false);
                        SetVigilanceAlarmDisplay(false);
                    }
                    else if (value == AlerterState.Stop)
                    {
                        penaltyBrake.Set();
                    }
                }
                else if (alerter == AlerterState.Stop)
                {
                    if (value == AlerterState.Countdown)
                    {
                        SetVigilanceAlarm(false);
                        SetVigilanceAlarmDisplay(false);
                        penaltyBrake.Release();
                    }
                    else if (value == AlerterState.Alert)
                    {
                        SetVigilanceAlarm(false);
                        SetVigilanceAlarmDisplay(false);
                        penaltyBrake.Release();
                        alerterTimer.Setup(CountdownS);
                        alerterTimer.Start();
                    }
                }

                alerter = value;
            }
        }

        public override void Initialize()
        {
            blockTracker = new BlockTracker(this);
            blockTracker.NewSignalBlock += HandleNewSignalBlock;
            penaltyBrake = new PenaltyBrake(this);
            vigilance = new Vigilance(this, GetFloatParameter("Alerter", "CountdownTimeS", 60f), GetBoolParameter("Alerter", "DoControlsReset", true));
            vigilance.Trip += HandleVigilanceTrip;
            currentCode = new CurrentCode(blockTracker, PulseCode.Clear125);
            changeZone = new CodeChangeZone(this, blockTracker);

            atc = ATCState.Off;
            atcTimer = new Timer(this);
            upgrade = UpgradeState.Off;
            upgradeTimer = new Timer(this);
            alerter = AlerterState.Countdown;
            alerterTimer = new Timer(this);

            Console.WriteLine("ATC initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            if (evt == TCSEvent.AlerterPressed)
            {
                if (Atc == ATCState.Countdown)
                    Atc = ATCState.Off;
                else if (Atc == ATCState.OverspeedCountdown)
                    Atc = ATCState.Overspeed;
                else if (Atc == ATCState.Stop && SpeedMpS() < 0.1f)
                    Atc = ATCState.Off;

                Alerter = AlerterState.Countdown;
                vigilance.Reset();
            }
        }

        private void HandleVigilanceTrip(object sender, EventArgs _)
        {
            if (Alerter == AlerterState.Countdown)
                Alerter = AlerterState.Alert;
        }

        private void HandleNewSignalBlock(object _, SignalBlockEventArgs e)
        {
            blockLengthM = e.BlockLengthM;
        }

        public override void SetEmergency(bool emergency)
        {
            SetEmergencyBrake(emergency);
        }

        public override void Update()
        {
            if (blockLengthM == TCSUtils.NullSignalDistance)
                blockLengthM = TCSUtils.NextSignalDistanceM(this, 0);

            if (Upgrade == UpgradeState.Play && upgradeTimer.Triggered)
                Upgrade = UpgradeState.Off;

            blockTracker.Update();
            UpdateCode();
            UpdateAlarm();
            UpdateAlerter();
        }

        private void UpdateCode()
        {
            float nextSignalM = TCSUtils.NextSignalDistanceM(this, 0);
            PulseCode thisCode = currentCode.GetCurrent();
            PulseCode changeCode = PulseCodeMapping.ToPriorPulseCode(TCSUtils.NextSignalAspect(this, 0));
            if (nextSignalM != TCSUtils.NullSignalDistance && nextSignalM <= MinStopZoneLengthM && changeCode == PulseCode.Restricting)
                DisplayCode = PulseCode.Restricting;
            else if (changeZone.Inside() && thisCode != PulseCode.Restricting)
                DisplayCode = changeCode;
            else
                DisplayCode = thisCode;

            SetNextSignalAspect(PulseCodeMapping.ToCabDisplay(DisplayCode));
            SetNextSpeedLimitMpS(PulseCodeMapping.ToSpeedMpS(DisplayCode));
        }

        private void UpdateAlarm()
        {
            if (!IsTrainControlEnabled())
            {
                if ((Atc == ATCState.Countdown || Atc == ATCState.OverspeedCountdown) && atcTimer.Triggered)
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

        private void UpdateAlerter()
        {
            if (IsTrainControlEnabled())
            {
                vigilance.Update();

                if (Alerter == AlerterState.Alert && alerterTimer.Triggered)
                    Alerter = AlerterState.Stop;
            }
            else
            {
                Alerter = AlerterState.Countdown;
            }
        }
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

internal class BlockTracker
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
                var e = new SignalBlockEventArgs(TCSUtils.NextSignalAspect(tcs, 0), TCSUtils.NextSignalDistanceM(tcs, 1));
                NewSignalBlock.Invoke(this, e);
            }

            signal = value;
        }
    }

    public BlockTracker(TrainControlSystem parent)
    {
        tcs = parent;
    }

    public void Update()
    {
        float distanceM = TCSUtils.NextSignalDistanceM(tcs, 0);
        Signal = distanceM != TCSUtils.NullSignalDistance && distanceM < 3f ? SignalPosition.Near : SignalPosition.Far;
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
        float nextDistanceM = TCSUtils.NextSignalDistanceM(tcs, 0);
        if (nextDistanceM == TCSUtils.NullSignalDistance)
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

internal static class TCSUtils
{
    public const float NullSignalDistance = 0f;
    public const Aspect NullSignalAspect = Aspect.None;

    public static float NextSignalDistanceM(TrainControlSystem tcs, int foresight)
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

    public static Aspect NextSignalAspect(TrainControlSystem tcs, int foresight)
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
                return PulseCode.CabSpeed80;
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
            case Aspect.Clear_1:
            case Aspect.Approach_2:
                return PulseCode.Clear125;
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
                return Aspect.Stop;
            default:
                return Aspect.None;
        }
    }
}

internal class CurrentCode
{
    private PulseCode code;

    public CurrentCode(BlockTracker blockTracker, PulseCode initCode)
    {
        blockTracker.NewSignalBlock += HandleNewSignalBlock;
        code = initCode;
    }

    public PulseCode GetCurrent()
    {
        return code;
    }

    public void HandleNewSignalBlock(object _, SignalBlockEventArgs e)
    {
        PulseCode newCode = PulseCodeMapping.ToPulseCode(e.Aspect);
        Console.WriteLine("ATC: {0} -> {1}", code, newCode);
        code = newCode;
    }
}

internal class PenaltyBrake
{
    private readonly TrainControlSystem tcs;
    private bool set = false;

    public PenaltyBrake(TrainControlSystem parent)
    {
        tcs = parent;
    }

    public void Set()
    {
        if (!set)
        {
            tcs.SetFullBrake(true);
            tcs.SetPenaltyApplicationDisplay(true);
            set = true;
        }
    }

    public void Release()
    {
        if (set)
        {
            tcs.SetFullBrake(false);
            tcs.SetPenaltyApplicationDisplay(false);
            set = false;
        }
    }
}

internal class Vigilance
{
    public event EventHandler Trip;

    private readonly TrainControlSystem tcs;
    private readonly List<IControlTracker> controls = new List<IControlTracker>();
    private readonly Timer timer;
    private readonly float countdownTimeS;
    private readonly bool doControlsReset;

    private interface IControlTracker
    {
        bool HasChanged();
    }
    private class ControlTracker<T> : IControlTracker where T : IEquatable<T>
    {
        private readonly MSTSLocomotive loco;
        private readonly Func<MSTSLocomotive, T> readValue;
        private IEquatable<T> state;

        public ControlTracker(MSTSLocomotive loco, Func<MSTSLocomotive, T> readValue)
        {
            this.loco = loco;
            this.readValue = readValue;
            state = readValue(loco);
        }

        public bool HasChanged()
        {
            IEquatable<T> newState = readValue(loco);
            bool changed = !newState.Equals(state);
            state = newState;
            return changed;
        }
    }

    public Vigilance(TrainControlSystem parent, float countdownTimeS, bool doControlsReset)
    {
        tcs = parent;
        timer = new Timer(tcs);
        this.countdownTimeS = countdownTimeS;
        this.doControlsReset = doControlsReset;

        if (doControlsReset)
        {
            var loco = tcs.Locomotive();
            controls.Add(new ControlTracker<float>(loco, (l) => l.ThrottleController.CurrentValue));
            controls.Add(new ControlTracker<float>(loco, (l) => l.DynamicBrakeController.CurrentValue));
            controls.Add(new ControlTracker<float>(loco, (l) => l.TrainBrakeController.CurrentValue));
            controls.Add(new ControlTracker<float>(loco, (l) => l.EngineBrakeController.CurrentValue));
        }
    }

    public void Update()
    {
        if (timer.Triggered)
        {
            timer.Stop();
            Trip.Invoke(this, EventArgs.Empty);
        }
        else if (countdownTimeS > 0 && tcs.IsAlerterEnabled() && tcs.SpeedMpS() >= 1f)
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

        bool movedControls = false;
        foreach (IControlTracker control in controls)
            if (control.HasChanged())
                movedControls = true;
        if (doControlsReset && movedControls)
            Reset();
    }

    public void Reset()
    {
        timer.Stop();
    }
}
