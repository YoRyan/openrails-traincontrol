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
using System.Linq;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_AMTK_ATC_ACSES : TrainControlSystem
    {
        private PenaltyBrake penaltyBrake;
        private OverspeedDisplay overspeed;
        private AlertSound alertSound;
        private Alerter alerter;
        private Atc atc;
        private Acses acses;
        private ISubsystem[] subsystems;

        private bool combineSpeedDisplays;

        public override void Initialize()
        {
            penaltyBrake = new PenaltyBrake(this);
            overspeed = new OverspeedDisplay(this);
            alertSound = new AlertSound(this);
            alerter = new Alerter(
                this,
                GetFloatParameter("Alerter", "CountdownTimeS", 60f),
                GetFloatParameter("Alerter", "AcknowledgeTimeS", 10f),
                GetBoolParameter("Alerter", "DoControlsReset", true),
                penaltyBrake.Set, penaltyBrake.Release);
            atc = new Atc(this, penaltyBrake, overspeed, alertSound);
            acses = new Acses(this, penaltyBrake, alertSound)
            {
                Enabled = GetBoolParameter("ACSES", "Enable", true)
            };
            subsystems = new ISubsystem[] { alerter, atc, acses };

            combineSpeedDisplays = !GetBoolParameter("ACSES", "UseSPEEDLIMIT", false);

            Console.WriteLine("Amtrak safety systems initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            foreach (ISubsystem sub in subsystems)
                sub.HandleEvent(evt, message);
        }

        public override void SetEmergency(bool emergency)
        {
            SetEmergencyBrake(emergency);
        }

        public override void Update()
        {
            foreach (ISubsystem sub in subsystems)
                sub.Update();

            SetNextSignalAspect(atc.CabAspect);
            if (combineSpeedDisplays && acses.Enabled)
            {
                SetNextSpeedLimitMpS(Math.Min(atc.SpeedLimitMpS, acses.SpeedLimitMpS));
                SetCurrentSpeedLimitMpS(0f);
            }
            else
            {
                SetNextSpeedLimitMpS(atc.SpeedLimitMpS);
                SetCurrentSpeedLimitMpS(acses.SpeedLimitMpS);
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
    public const float NullSpeedLimit = 0f;
    public const float NullPostDistance = 0f;

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

    public static float SafeCurrentPostSpeedLimitMpS(this TrainControlSystem tcs)
    {
        float speedLimitMpS;
        try
        {
            speedLimitMpS = tcs.CurrentPostSpeedLimitMpS();
        }
        catch (NullReferenceException)
        {
            return NullSpeedLimit;
        }
        return speedLimitMpS;
    }

    public static float SafeNextPostSpeedLimitMpS(this TrainControlSystem tcs, int forsight)
    {
        float speedLimitMpS;
        try
        {
            speedLimitMpS = tcs.NextPostSpeedLimitMpS(forsight);
        }
        catch (NullReferenceException)
        {
            return NullSpeedLimit;
        }
        return speedLimitMpS;
    }

    public static float SafeNextPostDistanceM(this TrainControlSystem tcs, int forsight)
    {
        float postDistanceM;
        try
        {
            postDistanceM = tcs.NextPostDistanceM(forsight);
        }
        catch (NullReferenceException)
        {
            return NullPostDistance;
        }
        return postDistanceM;
    }

    public static bool IsStopped(this TrainControlSystem tcs)
    {
        return tcs.SpeedMpS() < 0.1f;
    }
}

internal class Atc : ISubsystem
{
    public const float CountdownS = 6f;
    public const float UpgradeSoundS = 0.55f;
    public const Aspect InitAspect = Aspect.Clear_2;
    public const float SpeedLimitMarginMpS = 1.34f; // 3 mph
    public const float MinStopZoneLengthM = 457f; // 1500 ft
    // According to the Train Sim World: Northeast Corridor New York manual, these rates should be:
    //public const float SuppressingAccelMpSS = -0.5f;
    //public const float SuppressionAccelMpSS = -1.5f;
    // But these seem unachievable in Open Rails.
    public const float SuppressingAccelMpSS = -0.25f;
    public const float SuppressionAccelMpSS = -0.5f;

    private readonly TrainControlSystem tcs;
    private readonly Timer timer;
    private readonly BlockTracker blockTracker;
    private readonly CodeChangeZone changeZone;
    private readonly PenaltyBrake penaltyBrake;
    private readonly OverspeedDisplay overspeed;
    private readonly AlertSound alert;
    private readonly Sound upgradeSound;
    private readonly ISubsystem[] subsystems;
    private Aspect blockAspect = InitAspect;
    private float blockLengthM = TrainControlSystemExtensions.NullSignalDistance;

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
    private ATCState state = ATCState.Off;
    private ATCState State
    {
        get
        {
            return state;
        }
        set
        {
            if (state == ATCState.Off)
            {
                switch (value)
                {
                    case ATCState.Countdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case ATCState.OverspeedCountdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Set();
                        alert.Set();
                        break;
                    case ATCState.Overspeed:
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Set();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        break;
                }
            }
            else if (state == ATCState.Countdown)
            {
                switch (value)
                {
                    case ATCState.Off:
                    case ATCState.OverspeedSuppress:
                        alert.Release();
                        break;
                    case ATCState.OverspeedCountdown:
                        overspeed.Set();
                        break;
                    case ATCState.Overspeed:
                        overspeed.Set();
                        alert.Release();
                        break;
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Set();
                        alert.Release();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        alert.Release();
                        break;
                }
            }
            else if (state == ATCState.OverspeedCountdown)
            {
                switch (value)
                {
                    case ATCState.Off:
                        overspeed.Release();
                        alert.Release();
                        break;
                    case ATCState.Countdown:
                        overspeed.Release();
                        break;
                    case ATCState.Overspeed:
                        alert.Release();
                        break;
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Release();
                        break;
                    case ATCState.OverspeedSuppress:
                        overspeed.Release();
                        alert.Release();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        alert.Release();
                        overspeed.Release();
                        break;
                }
            }
            else if (state == ATCState.Overspeed)
            {
                switch (value)
                {
                    case ATCState.Off:
                    case ATCState.OverspeedSuppress:
                        overspeed.Release();
                        break;
                    case ATCState.Countdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Release();
                        alert.Set();
                        break;
                    case ATCState.OverspeedCountdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        overspeed.Release();
                        break;
                }
            }
            else if (state == ATCState.OverspeedSlowing)
            {
                switch (value)
                {
                    case ATCState.Off:
                        overspeed.Release();
                        break;
                    case ATCState.Countdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Release();
                        alert.Set();
                        break;
                    case ATCState.OverspeedCountdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case ATCState.Overspeed:
                        timer.Setup(CountdownS);
                        timer.Start();
                        break;
                    case ATCState.OverspeedSuppress:
                        overspeed.Release();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        overspeed.Release();
                        break;
                }
            }
            else if (state == ATCState.OverspeedSuppress)
            {
                switch (value)
                {
                    case ATCState.Countdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case ATCState.OverspeedCountdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case ATCState.Overspeed:
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        break;
                    case ATCState.Stop:
                        penaltyBrake.Set();
                        break;
                }
            }
            else if (state == ATCState.Stop)
            {
                switch (value)
                {
                    case ATCState.Off:
                        penaltyBrake.Release();
                        break;
                    case ATCState.Countdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        penaltyBrake.Release();
                        alert.Set();
                        break;
                    case ATCState.OverspeedCountdown:
                        timer.Setup(CountdownS);
                        timer.Start();
                        penaltyBrake.Release();
                        overspeed.Set();
                        alert.Set();
                        break;
                    case ATCState.Overspeed:
                    case ATCState.OverspeedSlowing:
                        timer.Setup(CountdownS);
                        timer.Start();
                        overspeed.Set();
                        penaltyBrake.Release();
                        break;
                    case ATCState.OverspeedSuppress:
                        penaltyBrake.Release();
                        break;
                }
            }

            if (state != value)
            {
                Console.WriteLine(string.Format("ATC: {0} -> {1}", state, value));
                state = value;
            }
        }
    }

    private PulseCode displayCode = PulseCodeMapping.ToPulseCode(InitAspect, 0f);
    private  PulseCode DisplayCode
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
                bool overspeed = speed != 0 && tcs.SpeedMpS() > speed + SpeedLimitMarginMpS;
                State = overspeed ? ATCState.OverspeedCountdown : ATCState.Countdown;
            }
            else if (value > displayCode)
            {
                upgradeSound.Play();
            }

            displayCode = value;
        }
    }

    public Aspect CabAspect { get { return PulseCodeMapping.ToCabDisplay(DisplayCode); } }
    public float SpeedLimitMpS { get { return PulseCodeMapping.ToSpeedMpS(DisplayCode); } }

    public Atc(TrainControlSystem parent, PenaltyBrake brake, OverspeedDisplay overspeed, AlertSound alert)
    {
        tcs = parent;
        timer = new Timer(tcs);
        blockTracker = new BlockTracker(tcs);
        blockTracker.NewSignalBlock += HandleNewSignalBlock;
        changeZone = new CodeChangeZone(tcs, blockTracker);
        penaltyBrake = brake;
        this.overspeed = overspeed;
        this.alert = alert;
        upgradeSound = new Sound(tcs, tcs.TriggerSoundInfo1, tcs.TriggerSoundInfo2, UpgradeSoundS);
        subsystems = new ISubsystem[] { blockTracker, upgradeSound };
    }

    private void HandleNewSignalBlock(object _, SignalBlockEventArgs e)
    {
        blockAspect = e.Aspect;
        blockLengthM = e.BlockLengthM;

        // Move the cab signal out of Restricting.
        if (DisplayCode == PulseCode.Restricting)
            DisplayCode = PulseCodeMapping.ToPulseCode(blockAspect, tcs.TrainSpeedLimitMpS());
    }

    public void HandleEvent(TCSEvent evt, string message)
    {
        foreach (ISubsystem sub in subsystems)
            sub.HandleEvent(evt, message);

        if (evt == TCSEvent.AlerterPressed)
        {
            if (State == ATCState.Countdown)
                State = ATCState.Off;
            else if (State == ATCState.OverspeedCountdown)
                State = ATCState.Overspeed;
            else if (State == ATCState.Stop && tcs.IsStopped())
                State = ATCState.Off;
        }
    }

    public void Update()
    {
        foreach (ISubsystem sub in subsystems)
            sub.Update();

        if (blockLengthM == TrainControlSystemExtensions.NullSignalDistance)
            blockLengthM = tcs.SafeNextSignalDistanceM(0);

        UpdateCode();
        UpdateAlarm();
    }

    private void UpdateCode()
    {
        if (tcs.GameTime() < 1f)
            return;

        float nextSignalM = tcs.SafeNextSignalDistanceM(0);
        float speedLimitMpS = tcs.TrainSpeedLimitMpS();
        PulseCode changeCode = PulseCodeMapping.ToPriorPulseCode(tcs.SafeNextSignalAspect(0), speedLimitMpS);
        if (nextSignalM != TrainControlSystemExtensions.NullSignalDistance && nextSignalM <= MinStopZoneLengthM && changeCode == PulseCode.Restricting)
            DisplayCode = PulseCode.Restricting;
        else if (DisplayCode == PulseCode.Restricting)
            DisplayCode = PulseCode.Restricting;
        else if (changeZone.Inside())
            DisplayCode = changeCode;
        else
            DisplayCode = PulseCodeMapping.ToPulseCode(blockAspect, speedLimitMpS);
    }

    private void UpdateAlarm()
    {
        if (!tcs.IsTrainControlEnabled())
        {
            if ((State == ATCState.Countdown || State == ATCState.OverspeedCountdown) && timer.Triggered)
                State = ATCState.Off;
            else if (State == ATCState.Overspeed)
                State = ATCState.Off;
            return;
        }

        float accelMpSS = tcs.Locomotive().AccelerationMpSS;
        float speed = PulseCodeMapping.ToSpeedMpS(DisplayCode);
        bool overspeed = speed != 0 && tcs.SpeedMpS() > speed + SpeedLimitMarginMpS;
        bool suppressing;
        switch (tcs.Locomotive().TrainBrakeController.TrainBrakeControllerState)
        {
            case ControllerState.Suppression:
            case ControllerState.ContServ:
            case ControllerState.FullServ:
            case ControllerState.Emergency:
                suppressing = true;
                break;
            default:
                suppressing = false;
                break;
        }

        if (State == ATCState.Off && overspeed)
        {
            State = ATCState.OverspeedCountdown;
        }
        else if ((State == ATCState.Countdown || State == ATCState.OverspeedCountdown) && timer.Triggered)
        {
            State = ATCState.Stop;
        }
        else if (State == ATCState.Overspeed)
        {
            if (!overspeed)
                State = ATCState.Off;
            else if (accelMpSS <= SuppressingAccelMpSS)
                State = ATCState.OverspeedSlowing;
            else if (suppressing)
                State = ATCState.OverspeedSuppress;
            else if (timer.Triggered)
                State = ATCState.Stop;
        }
        else if (State == ATCState.OverspeedSlowing)
        {
            if (!overspeed)
                State = ATCState.Off;
            else if (accelMpSS <= SuppressionAccelMpSS)
                State = ATCState.OverspeedSuppress;
            else if (accelMpSS > SuppressingAccelMpSS)
                State = ATCState.OverspeedCountdown;
            else if (suppressing)
                State = ATCState.OverspeedSuppress;
            else if (timer.Triggered)
                State = ATCState.Stop;
        }
        else if (State == ATCState.OverspeedSuppress)
        {
            if (!overspeed)
                State = ATCState.Off;
            else if (accelMpSS > SuppressionAccelMpSS && !suppressing)
                State = ATCState.OverspeedCountdown;
        }
    }
}

internal class Acses : ISubsystem
{
    public const float CountdownS = 6f;
    public const float PenaltyCurveMpSS = -0.89408f; // -2 mph/s
    public const float TimeToPenaltyS = 8f;
    public const float SpeedLimitAlertMpS = 0.44704f; // 1 mph
    public const float SpeedLimitPenaltyMpS = 1.34112f; // 3 mph

    private readonly TrainControlSystem tcs;
    private readonly Timer timer;
    private readonly PenaltyBrake penaltyBrake;
    private readonly AlertSound alert;
    private float offendingLimitMpS = 0f;

    private enum AcsesState
    {
        Off,
        Alert,
        AlertAcknowledged,
        Penalty,
        Recovered
    }
    private AcsesState state = AcsesState.Off;
    private AcsesState State
    {
        get
        {
            return state;
        }
        set
        {
            if (state == AcsesState.Off || state == AcsesState.AlertAcknowledged || state == AcsesState.Recovered)
            {
                switch (value)
                {
                    case AcsesState.Alert:
                        timer.Setup(CountdownS);
                        timer.Start();
                        alert.Set();
                        break;
                    case AcsesState.Penalty:
                        penaltyBrake.Set();
                        break;
                }
            }
            else if (state == AcsesState.Alert)
            {
                switch (value)
                {
                    case AcsesState.Off:
                    case AcsesState.AlertAcknowledged:
                    case AcsesState.Recovered:
                        alert.Release();
                        break;
                    case AcsesState.Penalty:
                        alert.Release();
                        penaltyBrake.Set();
                        break;
                }
            }
            else if (state == AcsesState.Penalty)
            {
                switch (value)
                {
                    case AcsesState.Off:
                    case AcsesState.AlertAcknowledged:
                    case AcsesState.Recovered:
                        penaltyBrake.Release();
                        break;
                    case AcsesState.Alert:
                        timer.Setup(CountdownS);
                        timer.Start();
                        penaltyBrake.Release();
                        alert.Set();
                        break;
                }
            }

            if (state != value)
            {
                Console.WriteLine(string.Format("ACSES: {0} -> {1}", state, value));
                state = value;
            }
        }
    }

    public float SpeedLimitMpS { get { return State == AcsesState.Off ? tcs.CurrentPostSpeedLimitMpS() : offendingLimitMpS; } }
    public bool Enabled = true;

    public Acses(TrainControlSystem parent, PenaltyBrake brake, AlertSound alert)
    {
        tcs = parent;
        timer = new Timer(tcs);
        penaltyBrake = brake;
        this.alert = alert;
    }

    public void HandleEvent(TCSEvent evt, string message)
    {
        if (evt == TCSEvent.AlerterPressed)
        {
            if (State == AcsesState.Alert)
                State = AcsesState.AlertAcknowledged;
            else if (State == AcsesState.Penalty && tcs.SpeedMpS() <= offendingLimitMpS)
                State = AcsesState.Recovered;
        }
    }

    public void Update()
    {
        if (!Enabled || !tcs.IsTrainControlEnabled())
        {
            State = AcsesState.Off;
            return;
        }

        float speedMpS = tcs.SpeedMpS();
        Func<SpeedPost, float, bool> inSpeedPostBrakeCurve = (SpeedPost post, float delayS) =>
        {
            const float slope = 0f;
            return post.DistanceM < tcs.DistanceCurve(speedMpS, post.LimitMpS, slope, delayS, -PenaltyCurveMpSS);
        };

        const int lookahead = 3;
        foreach (SpeedPost post in GetUpcomingSpeedPosts(lookahead))
        {
            if (inSpeedPostBrakeCurve(post, TimeToPenaltyS))
            {
                Alert(post.LimitMpS);
                break;
            }
            else if (inSpeedPostBrakeCurve(post, 0f))
            {
                Penalty(post.LimitMpS);
                break;
            }
        }

        float speedLimitMpS = tcs.SafeCurrentPostSpeedLimitMpS();
        bool speedLimitValid = speedLimitMpS != TrainControlSystemExtensions.NullSpeedLimit;
        if (State == AcsesState.Alert)
        {
            if (timer.Triggered)
                Penalty(offendingLimitMpS);
        }
        else if (State == AcsesState.Recovered || State == AcsesState.AlertAcknowledged)
        {
            if (speedLimitMpS <= offendingLimitMpS && speedLimitValid)
                State = AcsesState.Off;
        }

        if (speedMpS > speedLimitMpS + SpeedLimitPenaltyMpS && speedLimitValid)
            Penalty(speedLimitMpS);
        else if (speedMpS > speedLimitMpS + SpeedLimitAlertMpS && speedLimitValid)
            Alert(speedLimitMpS);
    }

    private struct SpeedPost
    {
        public float DistanceM;
        public float LimitMpS;
    }

    private IEnumerable<SpeedPost> GetUpcomingSpeedPosts(int number)
    {
        foreach (int i in Enumerable.Range(0, number))
        {
            float distanceM = tcs.SafeNextPostDistanceM(i);
            float limitMpS = tcs.SafeNextPostSpeedLimitMpS(i);
            if (distanceM == TrainControlSystemExtensions.NullPostDistance || limitMpS == TrainControlSystemExtensions.NullSpeedLimit)
                break;
            yield return new SpeedPost
            {
                DistanceM = distanceM,
                LimitMpS = limitMpS
            };
        }
    }

    private void Alert(float offendingLimitMpS)
    {
        this.offendingLimitMpS = offendingLimitMpS;
        State = AcsesState.Alert;
    }

    private void Penalty(float offendingLimitMpS)
    {
        this.offendingLimitMpS = offendingLimitMpS;
        State = AcsesState.Penalty;
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
    private const float mph2mps = 0.44704f;

    public static PulseCode ToPulseCode(Aspect aspect, float effectiveSpeedLimitMpS)
    {
        switch (aspect)
        {
            case Aspect.Clear_2:
                return effectiveSpeedLimitMpS > 125 * mph2mps ? PulseCode.Clear150 : PulseCode.Clear125;
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

    public static PulseCode ToPriorPulseCode(Aspect aspect, float speedLimitMpS)
    {
        switch (aspect)
        {
            case Aspect.Clear_2:
                return speedLimitMpS > 125 * mph2mps ? PulseCode.Clear150 : PulseCode.Clear125;
            case Aspect.Approach_2:
                return speedLimitMpS > 125 * mph2mps ? PulseCode.CabSpeed80 : PulseCode.Clear125;
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

internal class AlertSound : SharedLatch
{
    private readonly TrainControlSystem tcs;

    public AlertSound(TrainControlSystem parent)
    {
        tcs = parent;
    }

    protected override void DoSet()
    {
        tcs.TriggerSoundAlert1();
    }

    protected override void DoRelease()
    {
        tcs.TriggerSoundAlert2();
    }
}

internal class OverspeedDisplay : SharedLatch
{
    private readonly TrainControlSystem tcs;

    public OverspeedDisplay(TrainControlSystem parent)
    {
        tcs = parent;
    }

    protected override void DoSet()
    {
        tcs.SetOverspeedWarningDisplay(true);
    }

    protected override void DoRelease()
    {
        tcs.SetOverspeedWarningDisplay(false);
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
