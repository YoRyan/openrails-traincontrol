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
using System.Collections.Generic;
using System.Linq;
using Orts.Simulation.RollingStocks;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    class YoRyan_BNSF_ATS : TrainControlSystem
    {
        public const float SignalDistanceM = 15f;
        public const float CountdownSec = 8.0f;
        public float SignalActivateSpeedMpS = 17.8f; // 40 mph
        public float SpeedReductionDistM = 3218f; // 2 mi
        public float SpeedReductionDiffMpS = 8.94f; // 20 mph

        private PCSSwitch pcs;
        private Vigilance vigilance;
        private bool doControlsResetAlerter;

        private enum AlarmState
        {
            Off,
            Countdown,
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
                if (alarm == AlarmState.Off)
                {
                    if (value == AlarmState.Countdown)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: alert");
                        alarmTimer.Setup(CountdownSec);
                        alarmTimer.Start();
                        SetVigilanceAlarm(true);
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
                        SetVigilanceAlarm(false);
                    }
                }
                else if (alarm == AlarmState.Stop)
                {
                    if (value == AlarmState.Off)
                    {
                        Message(Orts.Simulation.ConfirmLevel.None, "ATS: acknowledge");
                        pcs.Release();
                        SetVigilanceAlarm(false);
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
            vigilance = new Vigilance(this, GetFloatParameter("Alerter", "CountdownTimeS", 60f));
            vigilance.Trip += HandleVigilanceTrip;
            doControlsResetAlerter = GetBoolParameter("Alerter", "DoControlsReset", true);

            alarm = AlarmState.Off;
            alarmTimer = new Timer(this);
            signal = GetSignalState();
            speed = GetSpeedState();

            Console.WriteLine("ATS initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            if (evt == TCSEvent.AlerterPressed)
            {
                if (Alarm == AlarmState.Countdown || Alarm == AlarmState.Stop)
                    Alarm = AlarmState.Off;

                vigilance.Reset();
            }

            if (doControlsResetAlerter)
            {
                switch (evt)
                {
                    case TCSEvent.ThrottleChanged:
                    case TCSEvent.TrainBrakeChanged:
                    case TCSEvent.EngineBrakeChanged:
                    case TCSEvent.DynamicBrakeChanged:
                    case TCSEvent.ReverserChanged:
                    case TCSEvent.GearBoxChanged:
                        vigilance.Reset();
                        break;
                }
            }
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
            if (!IsTrainControlEnabled())
            {
                pcs.InstantRelease();
                Alarm = AlarmState.Off;
                return;
            }

            if (Alarm == AlarmState.Countdown && alarmTimer.Triggered)
                Alarm = AlarmState.Stop;

            pcs.Update();
            vigilance.Update();
            Signal = GetSignalState();
            Speed = GetSpeedState();
        }

        private SignalState GetSignalState()
        {
            float distance;
            try
            {
                distance = NextSignalDistanceM(0);
            }
            catch (NullReferenceException)
            {
                return SignalState.Off;
            }
            var aspect = NextSignalAspect(0);
            bool restrictive = aspect != Aspect.Clear_2;
            return restrictive && AtDistance(distance, SignalDistanceM) && AtsActive() ? SignalState.Alert : SignalState.Off;
        }

        private SpeedState GetSpeedState()
        {
            float currentLimit;
            try
            {
                currentLimit = CurrentPostSpeedLimitMpS();
            }
            catch (NullReferenceException)
            {
                currentLimit = 0f;
            }

            const int lookahead = 3;
            SpeedPost[] posts = GetUpcomingSpeedPosts(lookahead);
            Func<int, bool> isNearPost = (forsight) =>
            {
                SpeedPost post = posts[forsight];
                float lastLimit = forsight == 0 ? currentLimit : posts[forsight - 1].LimitMpS;
                bool valid = post.LimitMpS != -1f && post.DistanceM != float.MaxValue;
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
                float distance, limit;
                try
                {
                    distance = NextPostDistanceM(i);
                }
                catch (NullReferenceException)
                {
                    distance = float.MaxValue;
                }
                try
                {
                    limit = NextPostSpeedLimitMpS(i);
                }
                catch (NullReferenceException)
                {
                    limit = -1f;
                }
                posts[i] = new SpeedPost { DistanceM = distance, LimitMpS = limit };
            }
            return posts;
        }

        private bool AtDistance(float distanceM, float targetDistanceM)
        {
            const float marginM = 3f;
            return targetDistanceM - marginM / 2 <= distanceM && distanceM <= targetDistanceM + marginM / 2;
        }

        private bool AtsActive()
        {
            float limit;
            try
            {
                limit = CurrentPostSpeedLimitMpS();
            }
            catch (NullReferenceException)
            {
                return false;
            }
            return limit >= SignalActivateSpeedMpS;
        }
    }
}

internal class PCSSwitch
{
    public const float ReleaseSec = 10f;

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
                    suppressed.Setup(ReleaseSec);
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

    public void Update()
    {
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

internal class Vigilance
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
    }

    public void Reset()
    {
        timer.Stop();
    }
}
