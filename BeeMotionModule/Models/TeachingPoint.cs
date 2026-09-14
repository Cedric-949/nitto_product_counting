using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BeeMotionModule.Models
{
    public enum NgAction
    {
        Continue = 0,       // Tiep tuc chu trinh binh thuong
        StopEarly = 1,      // Ngat chu trinh va bao loi ngay lap tuc
        JumpToPoint = 2     // Nhay den mot diem day xu ly NG (VD: Diem xa hang NG)
    }

    /// <summary>
    /// Represents a teaching point with axis position, speed, dwell time, and Vision Job mapping.
    /// </summary>
    public class TeachingPoint : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private int _id = 1;
        public int Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        private string _name = "Point 1";
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        private short _axisIndex = 0;
        public short AxisIndex
        {
            get => _axisIndex;
            set { if (_axisIndex != value) { _axisIndex = value; OnPropertyChanged(); } }
        }

        private double _position = 0.0;
        public double Position
        {
            get => _position;
            set { if (_position != value) { _position = value; OnPropertyChanged(); } }
        }

        private double _speed = 100.0;
        public double Speed
        {
            get => _speed;
            set { if (_speed != value) { _speed = value; OnPropertyChanged(); } }
        }

        private double _acceleration = 500.0;
        public double Acceleration
        {
            get => _acceleration;
            set { if (_acceleration != value) { _acceleration = value; OnPropertyChanged(); } }
        }

        private int _dwellTimeMs = 100;
        public int DwellTimeMs
        {
            get => _dwellTimeMs;
            set { if (_dwellTimeMs != value) { _dwellTimeMs = value; OnPropertyChanged(); } }
        }

        private bool _triggerVision = false;
        public bool TriggerVision
        {
            get => _triggerVision;
            set { if (_triggerVision != value) { _triggerVision = value; OnPropertyChanged(); } }
        }

        private int _jobId = 0;
        public int JobId
        {
            get => _jobId;
            set { if (_jobId != value) { _jobId = value; OnPropertyChanged(); } }
        }

        private NgAction _actionOnNg = NgAction.Continue;
        public NgAction ActionOnNg
        {
            get => _actionOnNg;
            set { if (_actionOnNg != value) { _actionOnNg = value; OnPropertyChanged(); } }
        }

        private int _targetPointIdOnNg = 0;
        public int TargetPointIdOnNg
        {
            get => _targetPointIdOnNg;
            set { if (_targetPointIdOnNg != value) { _targetPointIdOnNg = value; OnPropertyChanged(); } }
        }

        private int _setDoPinOnArrival = -1;
        public int SetDoPinOnArrival
        {
            get => _setDoPinOnArrival;
            set { if (_setDoPinOnArrival != value) { _setDoPinOnArrival = value; OnPropertyChanged(); } }
        }

        private bool _doStateOnArrival = true;
        public bool DoStateOnArrival
        {
            get => _doStateOnArrival;
            set { if (_doStateOnArrival != value) { _doStateOnArrival = value; OnPropertyChanged(); } }
        }

        // Process step coordination (like MotionVision)
        private string _stepType = "CheckVision";
        public string StepType
        {
            get => _stepType;
            set { if (_stepType != value) { _stepType = value; OnPropertyChanged(); } }
        }

        private int _stepOrder = 0;
        public int StepOrder
        {
            get => _stepOrder;
            set { if (_stepOrder != value) { _stepOrder = value; OnPropertyChanged(); } }
        }

        private double _timeoutMs = 5000.0;
        public double TimeoutMs
        {
            get => _timeoutMs;
            set { if (_timeoutMs != value) { _timeoutMs = value; OnPropertyChanged(); } }
        }

        public TeachingPoint Clone()
        {
            return new TeachingPoint
            {
                Id = this.Id,
                Name = this.Name,
                AxisIndex = this.AxisIndex,
                Position = this.Position,
                Speed = this.Speed,
                Acceleration = this.Acceleration,
                DwellTimeMs = this.DwellTimeMs,
                TriggerVision = this.TriggerVision,
                JobId = this.JobId,
                ActionOnNg = this.ActionOnNg,
                TargetPointIdOnNg = this.TargetPointIdOnNg,
                SetDoPinOnArrival = this.SetDoPinOnArrival,
                DoStateOnArrival = this.DoStateOnArrival,
                StepType = this.StepType,
                StepOrder = this.StepOrder,
                TimeoutMs = this.TimeoutMs
            };
        }
    }
}
