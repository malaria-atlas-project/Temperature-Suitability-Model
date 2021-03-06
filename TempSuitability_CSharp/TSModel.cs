﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MathNet.Numerics;
using MathNet.Numerics.Interpolation;

namespace TempSuitability_CSharp
{
    
    
    /// <summary>
    /// Class to run the temperature suitability model for a given cell (location), for the entire time period. 
    /// </summary>
    class TSCellModel
    {
        #region Fields
        private readonly PopulationParams modelParams;
        private readonly GeographicCell modelLocation;
        private IIterablePopulation m_Pop;

        private DateTime[] m_InputTemperatureDates;
        private bool _CanRun = false;
        private bool _PotentiallySuitableTemperature = false;
        
        private double m_PreviousSunsetTemp;
        private DateTime[] _iteratedOutputDates;
        private DateTime[] _calcOutputDates;
        /// <summary>
        /// The interpolation object for interpolating daily max temperatures from the input series at 
        /// e.g. 8-day intervals. X-values are time in seconds since the first input date.
        /// </summary>
        private IInterpolation _MaxTempSpline;
        /// <summary>
        /// The interpolation object for interpolating daily min temperatures from the input series at 
        /// e.g. 8-day intervals. X-values are time in seconds since the first input date.
        /// </summary>
        private IInterpolation _MinTempSpline;

        #endregion

        #region Properties
        /// <summary>
        /// Array of DateTimes corresponding in order to the dates of the outputs returned by RunModel, i.e. the first day
        /// of each calendar month within the input time period except for an initial 1 mosquito lifespan's worth
        /// </summary>
        public DateTime[] OutputDates
        {
            // calculate the output dates on demand because they're going to be the same for every TSCellModel run from the same 
            // set of input data, so it's a waste of effort to do it each time. Either return the values generated by actually 
            // running the model, if we have done so, or calculate what they would be, if we haven't run the model (not yet, or 
            // if the data aren't suitable so we can't).    
            get
            {
                if(_calcOutputDates != null) { return _calcOutputDates; }
                if(_iteratedOutputDates != null) { return _iteratedOutputDates; }
                var startDay = m_InputTemperatureDates.First();
                var endDay = m_InputTemperatureDates.Last();
                var mainRunStartDate = startDay + modelParams.MosquitoLifespanDays;
                var firstOfMonth = new DateTime(mainRunStartDate.Year, mainRunStartDate.Month, 1);
                var outMonths = new List<DateTime>();
                
                while (firstOfMonth < endDay)
                {
                    outMonths.Add(firstOfMonth);
                    firstOfMonth = firstOfMonth.AddMonths(1);
                }
                /*var outMonths = m_InputTemperatureDates             // all the input dates (e.g. 8 daily)
                   .Where(d => d >= mainRunStartDate)               // the input dates after the first lifespan
                   .Select(d => new DateTime(d.Year, d.Month, 1))   // change each of them to the first of that month
                   .Distinct()                                      // get unique
                   .OrderBy(d => d)                                 // sort
                   .ToArray();well                                      // return as array
                */
                int nMonths = outMonths.Count();
              
                if (_iteratedOutputDates != null && _iteratedOutputDates.Count() > 0 && !_iteratedOutputDates.SequenceEqual(outMonths))
                {
                    throw new ApplicationException("Debug, date calculation error");
                }
                _calcOutputDates = outMonths.ToArray();
                return _calcOutputDates;
            }
        }
        #endregion

        /// <summary>
        /// Constructs a new TSCellModel to calculate dynamic temperature suitability for Pf transmission, across time at a single 
        /// cell location. The underlying population can be modelled via any object implementing IIterablePopulation. Currently there 
        /// are three such classes which take different approaches to the iteration for performance testing; which of these to use 
        /// is currently specified by the PopulationTypes enum. After construction, call SetData before calling RunModel.
        /// </summary>
        /// <param name="modelConfigParams"></param>
        /// <param name="locationParams"></param>
        /// <param name="modelType"></param>
        public TSCellModel(PopulationParams modelConfigParams, GeographicCellLocation locationParams, PopulationTypes modelType)
        {
            this.modelParams = modelConfigParams;
            this.modelLocation = new GeographicCell(locationParams);
            double sliceLengthDays = modelParams.SliceLength.TotalDays;
            int lifespanSlices = modelConfigParams.LifespanSlices;
            switch (modelType)
            {
                case PopulationTypes.OOCohorts:
                    m_Pop = new PopulationOO(lifespanSlices, sliceLengthDays, 
                        modelConfigParams.MinTempThreshold, 
                        modelConfigParams.DegreeDayThreshold,
                        modelConfigParams.MosquitoDeathTemperature);
                    break;
                case PopulationTypes.Arrays:
                    m_Pop = new PopulationArray(lifespanSlices, sliceLengthDays, 
                        modelConfigParams.MinTempThreshold, 
                        modelConfigParams.DegreeDayThreshold,
                        modelConfigParams.MosquitoDeathTemperature);
                    break;
                case PopulationTypes.Pointers:
                    m_Pop = new PopulationPtr(lifespanSlices, sliceLengthDays, 
                        modelConfigParams.MinTempThreshold, 
                        modelConfigParams.DegreeDayThreshold, 
                        modelConfigParams.MosquitoDeathTemperature);
                    break;
            }
            
        }

        /// <summary>
        /// configures the data points to run this model. Provide three arrays, which must be of identical length, containing 
        /// recorded daytime temps, nighttime temps, and dates of those temps, which must all be of the same length and in 
        /// sorted order. If only one of the temperature series has a valid value at a particular point then the other array at
        /// that position should be set to NoDataValue.
        /// </summary>
        /// <param name="Max_Temps">Array of LST Day temperatures, one per MODIS file in the period e.g. one every 8 days</param>
        /// <param name="Min_Temps">Array of LST Night temperatures, one per MODIS file in the period e.g. one every 8 days. 
        /// Must be for the same dates as LST_Day input.</param>
        /// <param name="TemperatureDatePoints">Array of DateTime objects representing the dates of the input temperatures.</param>
        /// <returns></returns>
        public bool SetData(float[] Max_Temps, float[] Min_Temps, DateTime[] TemperatureDatePoints, float NoDataValue, 
            bool convertMaxTempsFromLST, bool convertMinTempsFromLST)
        {
            // check inputs are of correct length
            if (Max_Temps.Length != Min_Temps.Length ||
                Max_Temps.Length != TemperatureDatePoints.Length)
            {
                return false;
            }
            int nPoints = TemperatureDatePoints.Length;
            // check dates are sorted
            for (int i = 1; i < nPoints; i++)
            {
                if (TemperatureDatePoints[i - 1] > TemperatureDatePoints[i])
                {
                    return false;
                }
            }
            // Store the original input dates
            m_InputTemperatureDates = TemperatureDatePoints;
            var startDay = m_InputTemperatureDates.First();

            // For each of max and min temps, we will create a spline between time/value pairs but only for points where the 
            // temperature was valid (the spline doesn't know about nodata). So first generate separate list of valid time stamps 
            // for max and min. 
            List<double> validminTemps, validmaxTemps, validminDatePoints, validmaxDatePoints;
            // we will create the lists at their max possible size (i.e. same as input, all values are valid) and trim later,
            // rather than grow as needed, this is an attempt to fix a garbage collector crash that was occurring here in an 
            // old version of mono
            validminTemps = new List<double>(nPoints);
            validmaxTemps = new List<double>(nPoints);
            validmaxDatePoints = new List<double>(nPoints);
            validminDatePoints = new List<double>(nPoints);
            double minTemp, maxTemp;
            for (int i = 0; i<nPoints; i++)
            {
                if (Min_Temps[i] != NoDataValue)
                {
                    ConvertTemperature(Max_Temps[i], Min_Temps[i], TemperatureDatePoints[i].DayOfYear, out maxTemp, out minTemp);
                    // min temp calculation only depends on the night temp
                    if (convertMinTempsFromLST)
                    {
                        validminTemps.Add(minTemp);
                    }
                    else
                    {
                        // for development only, allow to run the model against unconverted LST values rather than converted air temp
                        validminTemps.Add(Min_Temps[i]);
                    }
                    // create a copy of the input image date as seconds since the first one, as the interpolator needs doubles not dates
                    var dSeconds = (TemperatureDatePoints[i] - startDay).TotalSeconds;
                    validminDatePoints.Add(dSeconds);

                    // max temp calculation depends on both the day and night temp
                    if (Max_Temps[i] != NoDataValue)
                    {
                        if (convertMaxTempsFromLST)
                        {
                            validmaxTemps.Add(maxTemp);
                        }
                        else
                        {
                            validmaxTemps.Add(Max_Temps[i]);
                        }
                        validmaxDatePoints.Add(dSeconds);
                    }
                }
            }
            if (validmaxTemps.Count / TemperatureDatePoints.Length < modelParams.ValidDataProportion
                &&
                validminTemps.Count / TemperatureDatePoints.Length < modelParams.ValidDataProportion)
            {
                return false;
            }

            // generate the spline interpolator objects to get daily temps from the 8-daily (or whatever) inputs
            // note we will use these splines for all days, rather than the original data on the days where they exist,
            // because splines don't have to pass through the data points and we don't want discontinuities
            // We are only storing (and splining from) the converted min/max air temps rather than the LST temps
            _MaxTempSpline = CubicSpline.InterpolateNaturalSorted(validmaxDatePoints.ToArray(), validmaxTemps.ToArray());
            _MinTempSpline = CubicSpline.InterpolateNaturalSorted(validminDatePoints.ToArray(), validminTemps.ToArray());
           
            // set the initial "previous" sunset temp to be the first day's max temp
            m_PreviousSunsetTemp = validmaxTemps.First();

            // set the flag for whether the temperatures are _ever_ suitable for sporogenesis
            if (validmaxTemps.Max() > modelParams.MinTempThreshold && validmaxTemps.Min() < modelParams.MosquitoDeathTemperature) {
                // NB the Weiss code checked on the 1-daily splined temperature values for this; here we 
                // are checking on the converted 8-daily (or whatever interval is provided) input values. 
                // A spline can go outside the range of the input data, so this doesn't necessarily give 
                // the same result in every case, but i think it's reasonable to not base our decisions on such cases.
                _PotentiallySuitableTemperature = true;
            }

            _CanRun = true;
            return true;
        }

        /// <summary>
        /// for development only, just generates and returns the entire 2-hourly interpolated temperature series at this point
        /// </summary>
        /// <returns></returns>
        public float[] GetTempSeries()
        {
            if (!_CanRun)
            {
                throw new InvalidOperationException("Data has not yet been loaded");
            }
            var startDay = m_InputTemperatureDates.First();
            var endDay = m_InputTemperatureDates.Last();

            TimeSpan oneDay = new TimeSpan(1, 0, 0, 0, 0);
            double sliceLengthDays = modelParams.SliceLength.TotalDays;
            int slicesPerDay = (int)(1.0 / sliceLengthDays);
            int sliceLengthHours = (int)modelParams.SliceLength.TotalHours;
            double maxAirTemp, minAirTemp;
           
            // get a local ref to the spline objects (just fractionally more efficient)
            var maxSpline = _MaxTempSpline;
            var minSpline = _MinTempSpline;
            int totalSlices = (int) (endDay - startDay).TotalDays * slicesPerDay;
            float[] sliceTemps = new float[totalSlices];
            int totalSliceNum = 0;
            for (DateTime currentDay = startDay; currentDay < endDay; currentDay += oneDay)
            {
                TimeSpan timeSinceStart = (currentDay - startDay);
                double secondsSinceStart = timeSinceStart.TotalSeconds;
                int runDay = timeSinceStart.Days;
                // interpolate the temp for this calendar day, from the 8-or-whatever-daily input series
                maxAirTemp = maxSpline.Interpolate(secondsSinceStart);
                minAirTemp = minSpline.Interpolate(secondsSinceStart);
                int julianDay = currentDay.DayOfYear;
                Tuple<double, double> sunriseSunset = modelLocation.GetSunriseSunsetTimes(julianDay);
                for (TimeSpan daySlice = new TimeSpan(0); daySlice < oneDay; daySlice += modelParams.SliceLength)
                {
                    double currentTemp = InterpolateHourlyTemperature(daySlice.Hours, sunriseSunset.Item1, sunriseSunset.Item2, minAirTemp, maxAirTemp);
                    sliceTemps[totalSliceNum] = (float)currentTemp;
                    totalSliceNum += 1;
                }
            }
            return sliceTemps;
        }

        /// <summary>
        /// Runs the temperature suitability model for this cell for the entire time period represented by the data,
        /// which must first have been loaded using SetData().
        /// Returns a float array with one value for each calendar month spanned by the input data, minus the length
        /// length of the "spin-up" period (1 mosquito lifetime). Dates of these points can be accessed via the OutputDates
        /// property. Note that internal model calculations are run in double precision and are downcast back to single 
        /// for output only.
        /// </summary>
        /// <returns>Array of float with one item for each month in the period, giving the TS value</returns>
        public float[] RunModel()
        {
            if (!_CanRun)
            {
                throw new InvalidOperationException("Data has not yet been loaded");
            }
            if (!_PotentiallySuitableTemperature){
                // IF the temperature never enters suitable range, return a new array (which will have the 
                // default value of zero, i.e. no temperature suitability) andof the same length as the 
                // output would have been i.e. the number of months in the input series excluding the 
                // first lifespan-worth.
                // The output dates will be calculated on demand at this point.
                int nMonths = OutputDates.Count();
                return new float[nMonths] ;
            }
            var startDay = m_InputTemperatureDates.First();
            var endDay = m_InputTemperatureDates.Last();
            var mainRunStartDate = startDay + modelParams.MosquitoLifespanDays;

            TimeSpan oneDay = new TimeSpan(1, 0, 0, 0, 0);
            double sliceLengthDays = modelParams.SliceLength.TotalDays;
            int slicesPerDay = (int)(1.0 / sliceLengthDays);
            int sliceLengthHours = (int)modelParams.SliceLength.TotalHours;
            int slicesPerLifespan = modelParams.LifespanSlices; // this rounds away any fractional days of the lifespan

            double[] tSpinUpData = new double[slicesPerLifespan];
            double maxAirTemp, minAirTemp;
            // get a local ref to the spline objects (just fractionally more efficient)
            var maxSpline = _MaxTempSpline;
            var minSpline = _MinTempSpline;

            // We will run through the overall data timespan from start to end, on a timestep of one calendar day. 
            // At each calendar day we will interpolate a max and min temperature value from the 8-daily converted 
            // input series, then we will run through the sub-daily (e.g. 2-hour) slices that comprise a day and 
            // interpolate temperature and calculate suitability at each such slice.

            // First "spin up" the model by running through the first mosquito-lifespan-worth of data so we 
            // have a stabilised population. Generate an array with a temperature for each 2-hour timeslice of a
            // a mosquito's life for a one-shot initialise call
            int spinupSliceNum = 0;
            for (DateTime currentDay = startDay; currentDay < mainRunStartDate; currentDay += oneDay)
            {
                TimeSpan timeSinceStart = (currentDay - startDay);
                double secondsSinceStart = timeSinceStart.TotalSeconds;
                int runDay = timeSinceStart.Days;
                // interpolate the temp for this calendar day, from the 8-or-whatever-daily input series
                maxAirTemp = maxSpline.Interpolate(secondsSinceStart);
                minAirTemp = minSpline.Interpolate(secondsSinceStart);
                int julianDay = currentDay.DayOfYear;
                Tuple<double, double> sunriseSunset = modelLocation.GetSunriseSunsetTimes(julianDay);
                for(TimeSpan daySlice = new TimeSpan(0); daySlice < oneDay; daySlice += modelParams.SliceLength)
                {
                    double currentTemp = InterpolateHourlyTemperature(daySlice.Hours, sunriseSunset.Item1, sunriseSunset.Item2, minAirTemp, maxAirTemp);
                    tSpinUpData[spinupSliceNum] = currentTemp;
                    spinupSliceNum += 1;
                }
            }
            // spin up the population model
            m_Pop.Initialise(tSpinUpData);
     
            // Now, run the model for the (entire) remainder of the time period, tracking the results on a monthly basis
            SortedList<DateTime, double> results = new SortedList<DateTime, double>();
            Dictionary<DateTime, int> daysInMonth = new Dictionary<DateTime, int>();
            // Iterate through the time period one calendar day at a time
            for (DateTime currentDay = mainRunStartDate; currentDay <= endDay; currentDay += oneDay)
            {
                double tsOfDay = 0;
                TimeSpan timeSinceStart = (currentDay - startDay);
                double secondsSinceStart = timeSinceStart.TotalSeconds;
                int runDay = timeSinceStart.Days;
                DateTime currentMonth = new DateTime(currentDay.Year, currentDay.Month, 1);
                
                // interpolate the temp for this calendar day, from the 8-or-whatever-daily input series
                maxAirTemp = maxSpline.Interpolate(secondsSinceStart);
                minAirTemp = minSpline.Interpolate(secondsSinceStart);
                int julianDay = currentDay.DayOfYear;
                Tuple<double, double> sunriseSunset = modelLocation.GetSunriseSunsetTimes(julianDay);
                // go through each e.g. 12 slices of this day
                for (TimeSpan daySlice = new TimeSpan(0); daySlice < oneDay; daySlice += modelParams.SliceLength)
                {
                    double currentTemp = InterpolateHourlyTemperature(daySlice.Hours, sunriseSunset.Item1, sunriseSunset.Item2, minAirTemp, maxAirTemp);
                    tsOfDay += m_Pop.Iterate(currentTemp);
                }
                // for each calendar month record the total (sum) temp suitability of all the 2-hour slices in that month
                if (results.ContainsKey(currentMonth)){
                    results[currentMonth] += tsOfDay;
                    // also count the number of calendar days in each month that we've calculated a value for (not necessarily a whole 
                    // month, at the start and end, and february varies too)
                    daysInMonth[currentMonth] += 1;
                }
                else
                {
                    results[currentMonth] = tsOfDay;
                    daysInMonth[currentMonth] = 1;
                }
            }
            var months = results.Keys.ToList();
            // Take the average slice value for the month, not the total
            foreach (var mth in months)
            {
                var divisor = (daysInMonth[mth] * slicesPerDay * modelParams.MaxTempSuitability);
                results[mth] /= divisor;
            }
            // store the months for which we've generated outputs so the OutputDates property doesn't have to calculate 
            // them if it's called
            _iteratedOutputDates = results.Keys.ToArray();
            // output as float (we ran using doubles, but no need to maintain that as input / output tiffs will be float32)
            return results.Values.Select(d => (float)d).ToArray();
        }
        
   
        /// <summary>
        /// Estimates max and min daily air temperature from the daytime and nighttime land surface temperatures, according 
        /// to the models published in Weiss et al. 2014. The calculation for max temperature takes account of the length of the day,
        /// which (NB) is calculated by a different model to the sunrise / sunset calculation used in interpolating intra-day temperatures.
        /// </summary>
        /// <param name="LST_Day_Temp"></param>
        /// <param name="LST_Night_Temp"></param>
        /// <param name="JulianDay"></param>
        /// <param name="MaxTemp"></param>
        /// <param name="MinTemp"></param>
        private void ConvertTemperature(double LST_Day_Temp, double LST_Night_Temp, int JulianDay, out double MaxTemp, out double MinTemp)
        {
            double LST_Diff = LST_Day_Temp - LST_Night_Temp;
            MinTemp = 0.209087 + 0.970841 * LST_Night_Temp;
            var dayHrs = modelLocation.CalcDaylightHrsForsyth(JulianDay);
            MaxTemp = 18.148887 + LST_Day_Temp* 0.949445 + (LST_Diff * -0.541052) +
                (dayHrs * -0.865620);
        }
         
        
        /// <summary>
        /// Estimates the temperature at a given hour of the day based on the daily max / min temperatures and the time 
        /// of sunrise and sunset, according to the model of Garske et al. 2013.
        /// </summary>
        /// <param name="hourOfDay"></param>
        /// <param name="sunriseTime"></param>
        /// <param name="sunsetTime"></param>
        /// <param name="dayMinTemp"></param>
        /// <param name="dayMaxTemp"></param>
        /// <returns></returns>
        private double InterpolateHourlyTemperature(double hourOfDay, double sunriseTime, double sunsetTime, double dayMinTemp, double dayMaxTemp)
        {
            double daylightHrs = sunsetTime - sunriseTime;
            double hrTemp;
            if (hourOfDay >= sunriseTime && hourOfDay <= sunsetTime)
            {
                hrTemp = dayMinTemp + (dayMaxTemp - dayMinTemp) *
                    (Math.Sin(Math.PI * (hourOfDay - sunriseTime) / (daylightHrs + 3.72)));
                m_PreviousSunsetTemp = hrTemp;
            }
            else if (hourOfDay > sunsetTime)
            {
                hrTemp = dayMinTemp + (m_PreviousSunsetTemp - dayMinTemp) *
                    Math.Exp(-2.2 * ((hourOfDay - sunsetTime) / (24 - daylightHrs)));
            }
            else // (hourOfDay < sunriseTime)
            {
                hrTemp = dayMinTemp + (m_PreviousSunsetTemp - dayMinTemp) *
                    Math.Exp(-2.2 * (((hourOfDay + 24) - sunsetTime) / (24 - daylightHrs)));
            }
            return hrTemp;
        }

   
    }
}
