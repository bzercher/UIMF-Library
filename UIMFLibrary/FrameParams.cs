﻿using System.Collections.Generic;

// ReSharper disable UnusedMember.Global

namespace UIMFLibrary
{
    /// <summary>
    /// Container for a set of frame parameters
    /// </summary>
    public class FrameParams
    {
        #region Structures

        /// <summary>
        /// Mass Calibration Coefficients
        /// </summary>
        public struct MassCalibrationCoefficientsType
        {
            /// <summary>
            /// Calibration Coefficient a2
            /// </summary>
            public double a2;

            /// <summary>
            /// Calibration Coefficient b2
            /// </summary>
            public double b2;

            /// <summary>
            /// Calibration Coefficient c2
            /// </summary>
            public double c2;

            /// <summary>
            /// Calibration Coefficient d2
            /// </summary>
            public double d2;

            /// <summary>
            /// Calibration Coefficient e2
            /// </summary>
            public double e2;

            /// <summary>
            /// Calibration Coefficient f2
            /// </summary>
            public double f2;
        }

        #endregion
        #region Member Variables

        /// <summary>
        /// Mass calibration coefficients are cached to allow for fast lookup via external classes
        /// </summary>
        /// <remarks>Do not make this an auto-property since this structure's members are updated directly in UpdateCachedParam</remarks>
#pragma warning disable IDE0032
        private MassCalibrationCoefficientsType mCachedMassCalibrationCoefficients;
#pragma warning restore IDE0032

        #endregion

        #region Properties

        /// <summary>
        /// Frame parameters dictionary
        /// </summary>
        /// <remarks>Key is parameter type; value is the frame parameter container (<see cref="FrameParam"/> class)</remarks>
        public Dictionary<FrameParamKeyType, FrameParam> Values { get; }

        /// <summary>
        /// Calibration slope
        /// </summary>
        /// <remarks>Returns 0 if not defined</remarks>
        public double CalibrationSlope
        {
            get
            {
                if (!HasParameter(FrameParamKeyType.CalibrationSlope))
                    return 0;

                return GetValueDouble(FrameParamKeyType.CalibrationSlope);
            }
        }

        /// <summary>
        /// Calibration intercept
        /// </summary>
        /// <remarks>Returns 0 if not defined</remarks>
        public double CalibrationIntercept
        {
            get
            {
                if (!HasParameter(FrameParamKeyType.CalibrationIntercept))
                    return 0;

                return GetValueDouble(FrameParamKeyType.CalibrationIntercept);
            }
        }

        /// <summary>
        /// Frame type
        /// </summary>
        /// <remarks>Returns MS1 if not defined</remarks>
        public UIMFData.FrameType FrameType
        {
            get
            {
                if (!HasParameter(FrameParamKeyType.FrameType))
                    return UIMFData.FrameType.MS1;

                var frameType = GetValueInt32(FrameParamKeyType.FrameType);
                if (frameType == 0)
                {
                    // This is an older UIMF file where the MS1 frames were labeled as 0
                    return UIMFData.FrameType.MS1;
                }

                return (UIMFData.FrameType)frameType;
            }
        }

        /// <summary>
        /// Mass calibration coefficients
        /// </summary>
        /// <remarks>Provided for quick reference to avoid having to access the dictionary and convert from string to double</remarks>
        public MassCalibrationCoefficientsType MassCalibrationCoefficients => mCachedMassCalibrationCoefficients;

        /// <summary>
        /// Scans per frame
        /// </summary>
        /// <remarks>Returns 0 if not defined</remarks>
        public int Scans
        {
            get
            {
                if (!HasParameter(FrameParamKeyType.Scans))
                    return 0;

                return GetValueInt32(FrameParamKeyType.Scans);
            }
        }

        #endregion

        /// <summary>
        /// Constructor
        /// </summary>
        public FrameParams()
        {
            Values = new Dictionary<FrameParamKeyType, FrameParam>();
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="value">Value (double)</param>
        public FrameParams AddUpdateValue(FrameParamKeyType paramType, double value)
        {
            return AddUpdateValueDynamic(paramType, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="value">Value (int)</param>
        public FrameParams AddUpdateValue(FrameParamKeyType paramType, int value)
        {
            return AddUpdateValueDynamic(paramType, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="value">Value (string)</param>
        public FrameParams AddUpdateValue(FrameParamKeyType paramType, string value)
        {
            return AddUpdateValueDynamic(paramType, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="value">Value (dynamic)</param>
        public FrameParams AddUpdateValue(FrameParamKeyType paramType, dynamic value)
        {
            return AddUpdateValueDynamic(paramType, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="value">Value (dynamic)</param>
        private FrameParams AddUpdateValueDynamic(FrameParamKeyType paramType, dynamic value)
        {
            if (Values.TryGetValue(paramType, out var paramEntry))
            {
                paramEntry.Value = value;
            }
            else
            {
                paramEntry = new FrameParam(FrameParamUtilities.GetParamDefByType(paramType), value);
                Values.Add(paramType, paramEntry);
            }

            UpdateCachedParam(paramType, value);

            return this;
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramDef">Frame parameter definition (<see cref="FrameParamDef"/> class)</param>
        /// <param name="value">Value (double)</param>
        public FrameParams AddUpdateValue(FrameParamDef paramDef, double value)
        {
            return AddUpdateValueDynamic(paramDef, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramDef">Frame parameter definition (<see cref="FrameParamDef"/> class)</param>
        /// <param name="value">Value (int)</param>
        public FrameParams AddUpdateValue(FrameParamDef paramDef, int value)
        {
            return AddUpdateValueDynamic(paramDef, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramDef">Frame parameter definition (<see cref="FrameParamDef"/> class)</param>
        /// <param name="value">Value (string)</param>
        public FrameParams AddUpdateValue(FrameParamDef paramDef, string value)
        {
            return AddUpdateValueDynamic(paramDef, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramDef">Frame parameter definition (<see cref="FrameParamDef"/> class)</param>
        /// <param name="value">Value (dynamic)</param>
        public FrameParams AddUpdateValue(FrameParamDef paramDef, dynamic value)
        {
            return AddUpdateValueDynamic(paramDef, value);
        }

        /// <summary>
        /// Add or update a parameter's value
        /// </summary>
        /// <param name="paramDef">Frame parameter definition (<see cref="FrameParamDef"/> class)</param>
        /// <param name="value">Value (dynamic)</param>
        private FrameParams AddUpdateValueDynamic(FrameParamDef paramDef, dynamic value)
        {
            if (Values.TryGetValue(paramDef.ParamType, out var paramEntry))
            {
                paramEntry.Value = value;
            }
            else
            {
                paramEntry = new FrameParam(paramDef, value);
                Values.Add(paramDef.ParamType, paramEntry);
            }

            return this;
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <returns>Value (dynamic)</returns>
        public dynamic GetValue(FrameParamKeyType paramType)
        {
            var defaultValue = FrameParamUtilities.GetDefaultValueByType(paramType);
            return GetValue(paramType, defaultValue);
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="valueIfMissing">Value to return if the parameter is not defined</param>
        /// <returns>Value (dynamic)</returns>
        public dynamic GetValue(FrameParamKeyType paramType, dynamic valueIfMissing)
        {
            if (Values.TryGetValue(paramType, out var paramEntry))
            {
                return paramEntry.Value;
            }

            return valueIfMissing;
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <returns>Value (double)</returns>
        /// <remarks>Returns 0 if not defined</remarks>
        public double GetValueDouble(FrameParamKeyType paramType)
        {
            return GetValueDouble(paramType, 0.0);
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="valueIfMissing">Value to return if the parameter is not defined</param>
        /// <returns>Value (double)</returns>
        public double GetValueDouble(FrameParamKeyType paramType, double valueIfMissing)
        {
            if (Values.TryGetValue(paramType, out var paramEntry))
            {
                if (FrameParamUtilities.ConvertDynamicToDouble(paramEntry.Value, out double result))
                    return result;
            }

            return valueIfMissing;
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <returns>Value (int)</returns>
        /// <remarks>Returns 0 if not defined</remarks>
        public int GetValueInt32(FrameParamKeyType paramType)
        {
            return GetValueInt32(paramType, 0);
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="valueIfMissing">Value to return if the parameter is not defined</param>
        /// <returns>Value (int)</returns>
        public int GetValueInt32(FrameParamKeyType paramType, int valueIfMissing)
        {
            if (Values.TryGetValue(paramType, out var paramEntry))
            {
                if (FrameParamUtilities.ConvertDynamicToInt32(paramEntry.Value, out int result))
                    return result;
            }

            return valueIfMissing;
        }

        /// <summary>
        /// Get the value for a parameter
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <param name="valueIfMissing">Value to return if the parameter is not defined</param>
        /// <returns>Value (dynamic)</returns>
        public string GetValueString(FrameParamKeyType paramType, string valueIfMissing = "")
        {
            return GetValue(paramType, valueIfMissing);
        }

        /// <summary>
        /// Lookup whether or not a frame parameter is defined
        /// </summary>
        /// <param name="paramType">Parameter type</param>
        /// <returns>True if defined, otherwise false</returns>
        public bool HasParameter(FrameParamKeyType paramType)
        {
            return Values.ContainsKey(paramType);
        }

        private void UpdateCachedParam(FrameParamKeyType paramType, dynamic value)
        {
            if (value == null)
                return;

            // Update cached member variables
            // At present, the only cached values are mass calibration coefficients

            switch (paramType)
            {
                case FrameParamKeyType.MassCalibrationCoefficienta2:
                    mCachedMassCalibrationCoefficients.a2 = (double)value;
                    break;
                case FrameParamKeyType.MassCalibrationCoefficientb2:
                    mCachedMassCalibrationCoefficients.b2 = (double)value;
                    break;
                case FrameParamKeyType.MassCalibrationCoefficientc2:
                    mCachedMassCalibrationCoefficients.c2 = (double)value;
                    break;
                case FrameParamKeyType.MassCalibrationCoefficientd2:
                    mCachedMassCalibrationCoefficients.d2 = (double)value;
                    break;
                case FrameParamKeyType.MassCalibrationCoefficiente2:
                    mCachedMassCalibrationCoefficients.e2 = (double)value;
                    break;
                case FrameParamKeyType.MassCalibrationCoefficientf2:
                    mCachedMassCalibrationCoefficients.f2 = (double)value;
                    break;
            }
        }
    }
}
