/*
 * BNSF Automatic Train Stop for Open Rails
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
using System.Linq;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_BNSF_ATS : TrainControlSystem
    {
        public const float SignalDistanceM = 15f;
        public const float CountdownS = 8.0f;
        public float SignalActivateSpeedMpS = 17.8f; // 40 mph
        public float SpeedReductionDistM = 3218f; // 2 mi
        public float SpeedReductionDiffMpS = 8.94f; // 20 mph

        private PCSSwitch pcs;
        private Alerter alerter;
        private AlerterSound alerterSound;
        private Timer alarmTimer;
        private ISubsystem[] subsystems;

        private enum AlarmState
        {
            Off,
            Countdown,
            Stop
        }
        private AlarmState alarm;
        private AlarmState Alarm
        {
            get
            {
                return alarm;
            }
            set
            {
                if (alarm == AlarmState.Off)
                {
                    if (value == AlarmState.Countdown)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: alert");
                        alarmTimer.Setup(CountdownS);
                        alarmTimer.Start();
                        alerterSound.Set();
                    }
                }
                else if (alarm == AlarmState.Countdown)
                {
                    if (value == AlarmState.Stop)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: penalty");
                        pcs.Trip();
                    }
                    else if (value == AlarmState.Off)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: acknowledge");
                        alerterSound.Release();
                    }
                }
                else if (alarm == AlarmState.Stop)
                {
                    if (value == AlarmState.Off)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: acknowledge");
                        pcs.Release();
                        alerterSound.Release();
                    }
                }

                if (alarm != value)
                {
                    Console.WriteLine("ATS Alarm: {0} -> {1}", alarm, value);
                    alarm = value;
                }
            }
        }

        private enum SignalState
        {
            Off,
            Alert
        }
        private SignalState signal;
        private SignalState Signal
        {
            get
            {
                return signal;
            }
            set
            {
                if (signal == SignalState.Off && value == SignalState.Alert)
                    Alarm = AlarmState.Countdown;

                if (signal != value)
                {
                    Console.WriteLine("ATS Signal: {0} -> {1}", signal, value);
                    signal = value;
                }
            }
        }

        private enum SpeedState
        {
            Off,
            Alert
        }
        private SpeedState speed;
        private SpeedState Speed
        {
            get
            {
                return speed;
            }
            set
            {
                if (speed == SpeedState.Off && value == SpeedState.Alert)
                    Alarm = AlarmState.Countdown;

                if (speed != value)
                {
                    Console.WriteLine("ATS Speed: {0} -> {1}", speed, value);
                    speed = value;
                }
            }
        }

        public override void Initialize()
        {
            Func<string, float, float, float> getScaledFloatParameter = (name, def, factor) =>
            {
                return GetFloatParameter("ATS", name, def / factor) * factor;
            };
            const float mph2mps = 0.44704f;
            SignalActivateSpeedMpS = getScaledFloatParameter("SignalActivateSpeedMPH", SignalActivateSpeedMpS, mph2mps);
            SpeedReductionDistM = getScaledFloatParameter("SpeedReductionDistMi", SpeedReductionDistM, mph2mps);
            SpeedReductionDiffMpS = getScaledFloatParameter("SpeedReductionDiffMPH", SpeedReductionDiffMpS, mph2mps);

            pcs = new PCSSwitch(this);
            alerterSound = new AlerterSound(this);
            alerter = new Alerter(
                this, alerterSound,
                GetFloatParameter("Alerter", "CountdownTimeS", 60f),
                GetFloatParameter("Alerter", "AcknowledgeTimeS", CountdownS),
                GetBoolParameter("Alerter", "DoControlsReset", true),
                pcs.Trip, pcs.Release);
            subsystems = new ISubsystem[] { pcs, alerter };

            alarm = AlarmState.Off;
            alarmTimer = new Timer(this);
            signal = GetSignalState();
            speed = GetSpeedState();

            Console.WriteLine("ATS initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            foreach (ISubsystem sub in subsystems)
                sub.HandleEvent(evt, message);

            if (evt == TCSEvent.AlerterPressed)
                if (Alarm == AlarmState.Countdown || Alarm == AlarmState.Stop)
                    Alarm = AlarmState.Off;
        }

        private void HandleVigilanceTrip(object sender, EventArgs _)
        {
            if (Alarm == AlarmState.Off)
                Alarm = AlarmState.Countdown;
        }

        public override void SetEmergency(bool emergency)
        {
            SetEmergencyBrake(emergency);
        }

        public override void Update()
        {
            foreach (ISubsystem sub in subsystems)
                sub.Update();

            if (IsTrainControlEnabled())
            {
                if (Alarm == AlarmState.Countdown && alarmTimer.Triggered)
                    Alarm = AlarmState.Stop;

                Signal = GetSignalState();
                Speed = GetSpeedState();
            }
            else
            {
                Alarm = AlarmState.Off;
            }
        }

        private SignalState GetSignalState()
        {
            var aspect = this.SafeNextSignalAspect(0);
            bool restrictive = aspect != Aspect.Clear_2;
            bool near = AtDistance(this.SafeNextSignalDistanceM(0), SignalDistanceM);
            return restrictive && near && AtsActive() ? SignalState.Alert : SignalState.Off;
        }

        private SpeedState GetSpeedState()
        {
            const int lookahead = 3;
            SpeedPost[] posts = GetUpcomingSpeedPosts(lookahead);
            Func<int, bool> isNearPost = (forsight) =>
            {
                SpeedPost post = posts[forsight];
                float lastLimit = forsight == 0 ? this.SafeCurrentPostSpeedLimitMpS() : posts[forsight - 1].LimitMpS;
                bool valid = post.LimitMpS != TrainControlSystemExtensions.NullSpeedLimit && post.DistanceM != TrainControlSystemExtensions.NullPostDistance;
                return valid && lastLimit - post.LimitMpS >= SpeedReductionDiffMpS && AtDistance(post.DistanceM, SpeedReductionDistM);
            };
            bool nearPost = false;
            foreach (int i in Enumerable.Range(0, lookahead))
                nearPost = nearPost || isNearPost(i);
            return AtsActive() && nearPost ? SpeedState.Alert : SpeedState.Off;
        }

        private struct SpeedPost
        {
            public float DistanceM;
            public float LimitMpS;
        }

        private SpeedPost[] GetUpcomingSpeedPosts(int number)
        {
            var posts = new SpeedPost[number];
            foreach (int i in Enumerable.Range(0, number))
            {
                posts[i] = new SpeedPost {
                    DistanceM = this.SafeNextPostDistanceM(i),
                    LimitMpS = this.SafeNextPostSpeedLimitMpS(i)
                };
            }
            return posts;
        }

        private bool AtDistance(float distanceM, float targetDistanceM)
        {
            const float marginM = 3f;
            bool valid = distanceM != TrainControlSystemExtensions.NullSignalDistance;
            return valid && targetDistanceM - marginM / 2 <= distanceM && distanceM <= targetDistanceM + marginM / 2;
        }

        private bool AtsActive()
        {
            return this.SafeCurrentPostSpeedLimitMpS() >= SignalActivateSpeedMpS;
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
    public const float NullPostDistance = 0f;
    public const float NullSpeedLimit = 0f;

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

internal class Alerter : ISubsystem
{
    private readonly TrainControlSystem tcs;
    private readonly AlerterSound alerterSound;
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
                    alerterSound.Set();
                    tcs.SetVigilanceAlarmDisplay(true);
                    timer.Setup(acknowledgeTimeS);
                    timer.Start();
                }
                else if (value == AlerterState.Stop)
                {
                    alerterSound.Set();
                    tcs.SetVigilanceAlarmDisplay(true);

                    setBrake();
                }
            }
            else if (state == AlerterState.Alarm)
            {
                if (value == AlerterState.Countdown)
                {
                    alerterSound.Release();
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
                    alerterSound.Release();
                    tcs.SetVigilanceAlarmDisplay(false);

                    releaseBrake();
                }
                else if (value == AlerterState.Alarm)
                {
                    alerterSound.Release();
                    tcs.SetVigilanceAlarmDisplay(false);
                    timer.Setup(acknowledgeTimeS);
                    timer.Start();

                    releaseBrake();
                }
            }

            state = value;
        }
    }

    public Alerter(TrainControlSystem parent, AlerterSound alerterSound, float countdownTimeS, float acknowledgeTimeS, bool doControlsReset, Action setBrake, Action releaseBrake)
    {
        tcs = parent;
        this.alerterSound = alerterSound;
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

internal class AlerterSound : SharedLatch
{
    private readonly TrainControlSystem tcs;

    public AlerterSound(TrainControlSystem parent)
    {
        tcs = parent;
    }

    protected override void DoSet()
    {
        tcs.SetVigilanceAlarm(true);
    }

    protected override void DoRelease()
    {
        tcs.SetVigilanceAlarm(false);
    }
}

internal class PCSSwitch : ISubsystem
{
    public const float ReleaseS = 10f;

    private readonly TrainControlSystem tcs;
    private readonly Timer suppressed;

    private enum SwitchState
    {
        Released,
        Tripped,
        Releasing,
        ReleasingSuppress
    }
    private SwitchState state = SwitchState.Released;
    private SwitchState State
    {
        get
        {
            return state;
        }
        set
        {
            if (state == SwitchState.Released && value == SwitchState.Tripped)
            {
                tcs.Message(Orts.Simulation.ConfirmLevel.None, "PCS: tripped");
                tcs.SetPenaltyApplicationDisplay(true);
                tcs.SetFullBrake(true);
            }
            else if (state == SwitchState.Tripped && value == SwitchState.Released)
            {
                tcs.SetPenaltyApplicationDisplay(false);
                tcs.SetFullBrake(false);
            }
            else if (state == SwitchState.Releasing)
            {
                if (value == SwitchState.Released)
                {
                    tcs.SetPenaltyApplicationDisplay(false);
                    tcs.SetFullBrake(false);
                }
                else if (value == SwitchState.ReleasingSuppress)
                {
                    tcs.Message(Orts.Simulation.ConfirmLevel.None, "PCS: releasing");
                    suppressed.Setup(ReleaseS);
                    suppressed.Start();
                }
            }
            else if (state == SwitchState.ReleasingSuppress)
            {
                if (value == SwitchState.Released)
                {
                    tcs.Message(Orts.Simulation.ConfirmLevel.None, "PCS: released");
                    tcs.SetPenaltyApplicationDisplay(false);
                    tcs.SetFullBrake(false);
                }
                else if (value == SwitchState.Releasing)
                {
                    suppressed.Stop();
                }
            }

            if (state != value)
            {
                Console.WriteLine("PCS: {0} -> {1}", state, value);
                state = value;
            }
        }
    }

    public PCSSwitch(TrainControlSystem parent)
    {
        tcs = parent;
        suppressed = new Timer(parent);
    }

    public void HandleEvent(TCSEvent evt, string message) {}

    public void Update()
    {
        if (!tcs.IsTrainControlEnabled())
        {
            InstantRelease();
            return;
        }

        if (State != SwitchState.Released && !tcs.DoesBrakeCutPower())
            tcs.SetThrottleController(0f);

        ControllerState brake = tcs.Locomotive().TrainBrakeController.TrainBrakeControllerState;
        bool suppressing = brake == ControllerState.Suppression || brake == ControllerState.ContServ || brake == ControllerState.FullServ;
        // Also reset the PCS with emergency braking.
        // I don't believe this is prototypical, but it is very difficult to gauge which brake notch you're in with TCS service braking activated.
        suppressing = suppressing || brake == ControllerState.Emergency;

        if (State == SwitchState.Releasing && suppressing)
        {
            State = SwitchState.ReleasingSuppress;
        }
        else if (State == SwitchState.ReleasingSuppress)
        {
            if (suppressed.Triggered)
            {
                State = SwitchState.Released;
            }
            else if (!suppressing)
            {
                State = SwitchState.Releasing;
            }
        }
    }

    public void Trip()
    {
        if (State == SwitchState.Released)
            State = SwitchState.Tripped;
    }

    public void Release()
    {
        if (State == SwitchState.Tripped)
            State = SwitchState.Releasing;
    }

    public void InstantRelease()
    {
        State = SwitchState.Released;
    }
}
