﻿/*
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
        public const float MinCodeChangeBlockLengthM = 1600f; // 1 mi

        private bool hasSpeedControl;
        private float blockLengthM = TCSUtils.NullSignalDistance;

        private BlockTracker blockTracker;
        private PenaltyBrake penaltyBrake;
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
                {
                    if (Alarm == AlarmState.Off && IsTrainControlEnabled())
                        Alarm = AlarmState.Countdown;
                }
                else
                {
                    Upgrade = UpgradeState.Play;
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

                if (alarm != value)
                {
                    Console.WriteLine(string.Format("CSS Alarm: {0} -> {1}", alarm, value));
                    alarm = value;
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
                    upgradeTimer.Setup(UpgradeSoundSec);
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

        public override void Initialize()
        {
            stopZone = StopZone.NotApplicable;
            blockTracker = new BlockTracker(this, HandleBlockChange);
            penaltyBrake = new PenaltyBrake(this);
            currentCode = new CurrentCode(this, PulseCode.Clear);
            changeZone = new CodeChangeZone(this);

            alarm = AlarmState.Off;
            alarmTimer = new Timer(this);
            upgradeTimer = new Timer(this);

            hasSpeedControl = GetBoolParameter("CSS", "SpeedControl", true);

            Console.WriteLine("CSS initialized!");
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            if (evt == TCSEvent.AlerterPressed)
            {
                if (Alarm == AlarmState.Countdown)
                {
                    float speed = PulseCodeMapping.ToSpeedMpS(DisplayCode);
                    bool overspeed = speed != 0 && SpeedMpS() > speed + SpeedLimitMarginMpS;
                    Alarm = overspeed ? AlarmState.Overspeed : AlarmState.Off;
                }
                else if (Alarm == AlarmState.Stop && SpeedMpS() < 0.1f)
                {
                    Alarm = AlarmState.Off;
                    penaltyBrake.Release();
                }
            }
        }

        private void HandleBlockChange(Aspect aspect, float blockLengthM)
        {
            currentCode.HandleBlockChange(aspect, blockLengthM);
            changeZone.HandleBlockChange(aspect, blockLengthM);

            // If passing another Approach signal, allow the displayed aspect to move back to Approach.
            if (currentCode.GetCurrent() == PulseCode.Approach)
                stopZone = StopZone.InApproach;

            this.blockLengthM = blockLengthM;
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
            UpdateCabDisplay();
            UpdateAlarm();
        }

        private void UpdateCabDisplay()
        {
            PulseCode code = currentCode.GetCurrent();
            var nextCode = PulseCodeMapping.ToPulseCode(TCSUtils.NextSignalAspect(this, 0));

            if (code == PulseCode.Approach && nextCode == PulseCode.Restricting && changeZone.Inside())
                stopZone = StopZone.Restricting;
            else if (code == PulseCode.Approach && stopZone != StopZone.Restricting)
                stopZone = StopZone.InApproach;
            else
                stopZone = StopZone.NotApplicable;

            // Once in Restricting, the displayed aspect should stay in Restricting.
            if (code == PulseCode.Restricting || stopZone == StopZone.Restricting)
                DisplayCode = PulseCode.Restricting;
            else if (Alarm == AlarmState.Off && blockLengthM >= MinCodeChangeBlockLengthM && nextCode > code)
                DisplayCode = nextCode;
            else
                DisplayCode = code;

            SetNextSignalAspect(PulseCodeMapping.ToCabDisplay(DisplayCode));
            SetNextSpeedLimitMpS(PulseCodeMapping.ToSpeedMpS(DisplayCode));
        }

        private void UpdateAlarm()
        {
            if (!IsTrainControlEnabled())
            {
                if (Alarm == AlarmState.Countdown && alarmTimer.Triggered)
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

internal class BlockTracker
{
    private readonly TrainControlSystem tcs;
    private readonly Action<Aspect, float> nextBlock;

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
                nextBlock(TCSUtils.NextSignalAspect(tcs, 0), TCSUtils.NextSignalDistanceM(tcs, 1));

            signal = value;
        }
    }

    public BlockTracker(TrainControlSystem parent, Action<Aspect, float> callback)
    {
        tcs = parent;
        nextBlock = callback;
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
        float nextDistanceM = TCSUtils.NextSignalDistanceM(tcs, 0);
        const float ft2m = 0.3048f;
        return nextDistanceM != TCSUtils.NullSignalDistance && nextDistanceM < Math.Max(blockLengthM / 2, 1500 * ft2m);
    }

    public void HandleBlockChange(Aspect _, float blockLengthM)
    {
        this.blockLengthM = blockLengthM;
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

    public void HandleBlockChange(Aspect aspect, float _)
    {
        PulseCode newCode = PulseCodeMapping.ToPulseCode(aspect);
        Console.WriteLine("CSS: {0} -> {1}", code, newCode);
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
