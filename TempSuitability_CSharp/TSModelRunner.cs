﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Configuration;
namespace TempSuitability_CSharp
{
    class TSModelRunner
    {
        private static readonly object _lockobj = new object();
        private readonly TiffCubeReader _maxReader;
        private readonly TiffCubeReader _minReader;
        private readonly IFilenameDateParser _fnParser;
        //private readonly string _maskPath;
        //private readonly string _outDir;
        //private readonly float _maskValidValue;
        //private readonly float _maskNDV;
        private readonly TSIModelConfig _cfg;
        private DateTime[] _outputDates;

        static int Main(string[] args)
        {
            var cfg = loadConfig(args);
            if (cfg == null)
            {
                return 1;
            }

            Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH")
      + ";C:\\Users\\zool1301.NDPH\\Documents\\Code_General\\temp-suitability\\TempSuitability_CSharp\\packages\\GDAL.Native.1.11.1\\gdal\\x64");
            
            TSModelRunner runner = new TSModelRunner(new FilenameDateParser_AutoDetect(), cfg);

            Stopwatch sw = new Stopwatch();
            sw.Start();
            runner.RunAllTiles();                                                                                                                                                               
            sw.Stop();
            Console.WriteLine("Time elapsed running model = {0}", sw.Elapsed);
            //Console.ReadKey();
            return 0;
        }

        /// <summary>
        /// If a command-line argument was passed, attempt to load it as a configuration file and 
        /// set up the model accordingly. If the input is a folder, not a file, then write a sample 
        /// config file into that folder as a template for the user to edit. If no command-line 
        /// argument was given, prompt for one.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static TSIModelConfig loadConfig(string[] args)
        {
            string runtimeConfigFile;
            if (args.Length == 0)
            {
                Console.WriteLine("Please specify a path to a configuration xml file.\n\n"+
                    "Alternatively specify a path to a folder, to create a sample config file \n in this folder for editing.\n\n"+
                    "Or, just hit enter to attempt to run with application default paths.");
                Console.Write("> ");
                runtimeConfigFile = Console.ReadLine();
            }
            else
            {
                runtimeConfigFile = args[0];
            }
            if (runtimeConfigFile == "")
            {
                runtimeConfigFile = null;
            }
            return TSIModelConfig.LoadOrCreateConfig(runtimeConfigFile);
        }

        static void setConfigFileAtRuntime(string[] args)
        {
            string runtimeConfigFile;
            if (args.Length == 0)
            {
                Console.WriteLine("Please specify a config file:");
                Console.Write("> ");
                runtimeConfigFile = Console.ReadLine();
            }
            else
            {
                runtimeConfigFile = args[0];
            }
            AppConfig.Change(runtimeConfigFile);
        }
        // see also https://www.codeproject.com/Articles/14465/Specify-a-Configuration-File-at-Runtime-for-a-C-Co


        private TSModelRunner(IFilenameDateParser _parser, TSIModelConfig cfg)
        {
            _fnParser = _parser;
            //var set = Properties.Settings.Default;
            //System.Console.WriteLine("Looking for files in " + m_FileWildCard);
            //var d = new System.Diagnostics.DefaultTraceListener();
            
            _maxReader = new TiffCubeReader(cfg.dataPathConfig.MaxTempFiles, _fnParser, 
                cfg.modelRunConfig.ReadFromDate, cfg.modelRunConfig.ReadToDate);
            var nMax = _maxReader.Filenames.Count;
            Console.WriteLine("Looking for max temp files in " + cfg.dataPathConfig.MaxTempFiles + 
                " - found "+ nMax.ToString());
            _minReader = new TiffCubeReader(cfg.dataPathConfig.MinTempFiles, _fnParser,
                cfg.modelRunConfig.ReadFromDate, cfg.modelRunConfig.ReadToDate);
            var nMin = _minReader.Filenames.Count;
            Console.WriteLine("Looking for min temp files in " + cfg.dataPathConfig.MinTempFiles +
                " - found " + nMin.ToString());

            if (nMax == 0 || nMin == 0)
            {
                throw new ArgumentException("Can't continue without data");
                // I don't see any reason not to continue if the numbers for day and night aren't actually equal
            }
            if (!_maxReader.GeoTransform.SequenceEqual(_minReader.GeoTransform))
            {
                throw new ArgumentException("max and min temp image transforms do not match!");
            }
            if (!GDAL_Operations.GetGeoTransform(cfg.dataPathConfig.MaskFile).SequenceEqual(_maxReader.GeoTransform))
            {
                throw new ArgumentException("Land-sea mask doesn't match images!");
            }
            if (!System.IO.Directory.Exists(cfg.dataPathConfig.OutputFolder))
            {
                System.IO.Directory.CreateDirectory(cfg.dataPathConfig.OutputFolder);
            }
            _cfg = cfg;
            
        }

        /// <summary>
        /// Runs TS model for all cells of all tiles required to cover the given bounding box at the given tile size.
        /// Any tile wholly in the sea will be skipped (no output files generated)
        /// </summary>
        /// <param name="WestDegrees"></param>
        /// <param name="EastDegrees"></param>
        /// <param name="NorthDegrees"></param>
        /// <param name="SouthDegrees"></param>
        /// <param name="TileSize"></param>
        public void RunAllTiles()
        {
            var globalGT = _maxReader.GeoTransform;
            var lims = _cfg.spatialLimits;
            var pxOverall = GDAL_Operations.CalculatePixelCoordsOfBlock(globalGT, 
                lims.WestLimitDegrees, lims.EastLimitDegrees, 
                lims.NorthLimitDegrees, lims.SouthLimitDegrees
            );
            var TileSize = _cfg.modelRunConfig.MaxTileSizePx;
            var latsToRun = _maxReader.GetSubsetLatitudeCoords((int)pxOverall.NorthPixelCoord, (int)(pxOverall.SouthPixelCoord - pxOverall.NorthPixelCoord));
            var lonsToRun = _maxReader.GetSubsetLongitudeCoords((int)pxOverall.WestPixelCoord, (int)(pxOverall.EastPixelCoord - pxOverall.WestPixelCoord));
            var nTilesX = (int)Math.Ceiling((double)lonsToRun.Length / TileSize);
            var nTilesY = (int)Math.Ceiling((double)latsToRun.Length / TileSize);

            var nTilesTotal = nTilesX * nTilesY;
            string tileDir = lims.WestLimitDegrees.ToString() + "W-"
                            + lims.EastLimitDegrees.ToString() + "E-"
                            + lims.NorthLimitDegrees.ToString() + "N-"
                            + lims.SouthLimitDegrees.ToString() + "S-"
                            + TileSize.ToString() +"px_tiles";
            Console.WriteLine("Initiating run of  " + nTilesTotal.ToString() + " tiles");
            var runDir = nTilesTotal == 1 ? 
                _cfg.dataPathConfig.OutputFolder : 
                System.IO.Path.Combine(_cfg.dataPathConfig.OutputFolder, tileDir);
            System.IO.Directory.CreateDirectory(runDir);
            for (int tileRow = 0; tileRow < nTilesY; tileRow++)
            {
                int yOff = (int) pxOverall.NorthPixelCoord + tileRow * TileSize;
                int yEnd = yOff + TileSize;
                int thisTileYSize = TileSize;
           
                if (yEnd > pxOverall.SouthPixelCoord)
                {
                    thisTileYSize = (int) pxOverall.SouthPixelCoord - yOff;
                }
                for (int tileCol = 0; tileCol < nTilesX; tileCol++)
                {
                    var tileNum = tileRow * nTilesX + tileCol + 1;
                    string tilenameLocPart;
                    if (nTilesTotal > 1)
                    {
                        tilenameLocPart = ".r" + tileRow.ToString("D3") + "_c" + tileCol.ToString("D3") + ".tif";
                    }
                    else
                    {
                        tilenameLocPart = ".tif";
                    }
                    if (System.IO.Directory.EnumerateFiles(runDir, "*" + tilenameLocPart).Count() != 0)
                    {
                        System.Console.WriteLine("Tile " + tileNum.ToString() + " appears to be already done, skipping");
                        continue;
                    }
                    int xOff = (int) pxOverall.WestPixelCoord + tileCol * TileSize;
                    int xEnd = xOff + TileSize;
                    int thisTileXSize = TileSize;
                    if (xEnd > pxOverall.EastPixelCoord)
                    {
                        thisTileXSize = (int)pxOverall.EastPixelCoord - xOff;
                    }

                    var tileCoords = new PixelLims(xOff, xOff + thisTileXSize, yOff, yOff + thisTileYSize);
                    var cellRes = RunTile(xOff, yOff, thisTileXSize, thisTileYSize);
                    if (cellRes.Length == 0)
                    {
                        // whole tile was in masked / sea area. Do not bother to write output.
                        Console.WriteLine("Tile "+tileNum.ToString() +" was wholly in sea - skipped");
                        continue;
                    }
                    Console.WriteLine("Tile computation completed, writing output");
                    var tileRes = TransposeCellData(
                        cellRes,
                        thisTileXSize, thisTileYSize);
                    var tileGT = _maxReader.GetSubsetGeoTransform(tileCoords);
                    var tileProj = _maxReader.Projection;
                    // write each tile to a separate tiff file - we can mosaic them later.
                    var tileLims = new PixelLims(0, thisTileXSize, 0, thisTileYSize);
                    for (int t = 0; t<tileRes.Length; t++)
                    {
                        var tData = tileRes[t];
                        string fn = _cfg.modelRunConfig.OutputFileTag + "." + 
                            _outputDates[t].Date.ToString("yyyy.MM") + tilenameLocPart;
                        string outFile = System.IO.Path.Combine(runDir, fn);
                        GDAL_Operations.WriteWholeTiff(outFile, tData, tileGT, tileProj, tileLims, true, _maxReader.NoDataValue);
                    }
                    Console.WriteLine("Tile " + tileNum.ToString() + " finished - wrote "+tileRes.Length.ToString()+" files");
                }
            }
        }

        /// <summary>
        /// Runs a temperature suitability model for all pixels in a region specified by pixel limits.
        /// Output is a jagged array with one value for each cell starting at top left and then row by 
        /// row to bottom right, EXCEPT if no pixel in the tile is in a data area in which case the output 
        /// is an array with length zero.
        /// Otherwise, each value is an array with one TS value for each month of the run period, 
        /// EXCEPT if the cell is in the sea / masked area, in which case it is an array of length 0.
        /// Each cell location is done independently and this is therefore multithreaded.
        /// </summary>
        /// <param name="xOff"></param>
        /// <param name="yOff"></param>
        /// <param name="xSize"></param>
        /// <param name="ySize"></param>
        /// <returns></returns>
        public float[][] RunTile(int xOff, int yOff, int xSize, int ySize)
        {
            // test area: int xOff=20480, int yOff=9216, int xSize=512, int ySize=512
            var lsMask = GDAL_Operations.ReadGDALRasterBandsToFlatArray(
                _cfg.dataPathConfig.MaskFile,
                xSize, ySize, xOff, yOff, 1);
            if (!lsMask.Any(v => v == _cfg.modelRunConfig.MaskValidValue))
            {
                // this whole tile is in the sea, no need to run, return as a special case a zero length cell array
                return new float[0][];
            }
            var maxTempData = _maxReader.ReadCellData(xOff, yOff, xSize, ySize);
            var lats = _maxReader.GetSubsetLatitudeCoords(yOff, ySize);
            var lons = _maxReader.GetSubsetLongitudeCoords(xOff, xSize);
            var minTempData = _minReader.ReadCellData(xOff, yOff, xSize, ySize);
            int numCells = xSize * ySize;

            // there's no reason in principle why we couldn't allow different numbers of min and max temp files although of 
            // course we'd have to pass separate dates arrays into the model then, but hey. It would be a bit more effort 
            // to handle different geotransforms in the max and min temps data and this way we make it less likely that 
            // they've come from separate sources.
            Debug.Assert(minTempData.Length == maxTempData.Length);
            Debug.Assert(maxTempData.Length == numCells);
            var dates = _maxReader.Filedates.ToArray();
            var nFiles = dates.Length;

            // get the model parameters from the default settings file, bearing in mind that this could have been re-set 
            // at initialisation to a file specified on the commandline, rather than just being the default app config
            //var set = Properties.Settings.Default;
            var set = _cfg.modelConfig;
            PopulationParams popParams = new PopulationParams();
            popParams.DegreeDayThreshold = set.SporogenesisDegreeDays;
            popParams.MinTempThreshold = set.MinTempThresholdCelsius;
            popParams.MosquitoDeathTemperature = set.DeathTempCelsius; ;
            popParams.MosquitoLifespanDays = new TimeSpan((int)set.LifespanDays, 0, 0, 0); ;
            popParams.SliceLength = new TimeSpan((int)set.SliceLengthHours, 0, 0);
            popParams.MaxTempSuitability = set.MaxTSNormaliser;
            // max ts for default settings is 34.2467; the Weiss code had 33.89401 , not sure how generated. 
            // I got this using iterative solver in excel.
            Console.WriteLine("Tile data loaded, computation beginning");

            var mConfig = _cfg.modelRunConfig;

            float[][] tsOut = new float[numCells][];
            DateTime[] outputDates = null;
            ParallelOptions b = new ParallelOptions();
            
            if (mConfig.MaxThreads != 0)
            {
                // set threads to 1 for easier step-through debugging or some other number to not hog the whole machine
                b.MaxDegreeOfParallelism = (int)mConfig.MaxThreads;
                //System.Threading.ThreadPool.SetMaxThreads(set.MaxThreads, set.MaxThreads);
                //System.Threading.ThreadPool.SetMinThreads(set.MinThreads, set.MinThreads);

            }
            int testnum = 0;
            while (outputDates == null && testnum < numCells)
            {
                // if we haven't got at least 50% of the data and 100+ points it's probably crap 
                // (unless we're playing with synoptic data or something, so make this threshold configurable).
                // This doesn't affect the date calculation but the spline will throw an error 
                // on initialisation
                var nValid = Math.Min(
                    maxTempData[testnum].Count(v => v != _maxReader.NoDataValue),
                    minTempData[testnum].Count(v => v != _minReader.NoDataValue));
                if (nValid < nFiles / 2 || nValid < mConfig.MinRequiredDataPoints)
                {
                    testnum += 1;
                    continue;
                }
                // set up a dummy model purely to parse the output dates (that they will all share)
                // avoids the need to test for the first one to do this in the parallel loop, which needs a lock,
                // which slows things right down with >>20 cores
                TSCellModel tsModel = new TSCellModel(
                popParams, 
                new GeographicCellLocation() {Latitude=lats[0], Longitude=lons[0]}, 
                PopulationTypes.Pointers);
                if (!tsModel.SetData(maxTempData[testnum], minTempData[testnum], dates, _maxReader.NoDataValue.Value, 
                    mConfig.MaxTempFilesAreLST, mConfig.MinTempFilesAreLST))
                {
                    throw new ApplicationException("Pop goes the weasel");
                };
                outputDates = tsModel.OutputDates;
                break;
            }

            Parallel.For(0, numCells, b, c =>
            {
                if (lsMask[c] != mConfig.MaskValidValue)
                {
                    // this cell is in the sea, no need to run, return as a special case a zero length result array
                    tsOut[c] = new float[0];
                }
                else
                {
                    int rownum = c / xSize;
                    int colnum = c % xSize;
                    GeographicCellLocation geogParams = new GeographicCellLocation();
                    geogParams.Latitude = lats[rownum];
                    geogParams.Longitude = lons[colnum];

                    //geogParams.ModelRuntimeDays = nDays;
                    //geogParams.StartJulianDay = 0;

                    // run only if we've got at least 50% of the data and 100+ points (or whatever is configured)
                    var nValid = Math.Min(
                        maxTempData[c].Count(v => v != _maxReader.NoDataValue),
                        minTempData[c].Count(v => v != _minReader.NoDataValue));
                    if (nValid < nFiles / 2 || nValid < mConfig.MinRequiredDataPoints)
                    {
                        tsOut[c] = new float[0];
                    }
                    else
                    {
                        TSCellModel tsModel = new TSCellModel(popParams, geogParams, PopulationTypes.Pointers);
                       // var dd = dayData[c];
                       // var nd = nightData[c];
                       // float optimal = 28.6857194664029F;
                       // for (int i = 0; i < 727; i++) {
                       //     dd[i] = optimal;
            //                if (i%3==0) { dd[i] = 43; }
                       //     nd[i] = optimal; }
                        //tsModel.SetData(dd, nd, dates, _maxReader.NoDataValue.Value, false);

                        if (!tsModel.SetData(maxTempData[c], minTempData[c], dates, _maxReader.NoDataValue.Value, 
                            mConfig.MaxTempFilesAreLST, mConfig.MinTempFilesAreLST))
                        {
                            throw new ApplicationException("Pop goes the weasel");
                        };
                        // run the entire ts model for this location
                        float[] tsCell = tsModel.RunModel();
                        int nOutputPoints = tsCell.Length;
                        tsOut[c] = tsCell;
                        /*lock (_lockobj) // ensure only 1 thread makes this check 
                        {
                            // calculate and record the dates of the outputs for an arbitrary one of the cell models
                            if (outputDates == null)
                            {
                                outputDates = tsModel.OutputDates;
                            }
                        }*/
                    }
                }
            }
            );
            if (_outputDates == null)
            {
                _outputDates = outputDates;
            }
            else if (outputDates != null && !_outputDates.SequenceEqual(outputDates))
            {
                throw new ApplicationException("Dates have changed between tiles somehow, this shouldn't happen...");
            }
            return tsOut;
        }

        /// <summary>
        /// Transposes a jagged array - from cell * time, to time * cell.
        /// Each value of the output therefore represents the cell values across a tile 
        /// at a particular time point, and can be written to a tiff file of that time.
        /// </summary>
        /// <param name="CellData"></param>
        /// <param name="xSize"></param>
        /// <param name="ySize"></param>
        /// <returns></returns>
        private float[][] TransposeCellData(float[][] CellData, int xSize, int ySize)
        {
            var nCells = xSize * ySize;
            if (nCells != CellData.Length)
            {
                throw new ArgumentException("Specified tile shape does not match the given number of cells");
            }
            int nTiles;
            try {
                nTiles = CellData.First(cd => cd.Length != 0).Length;
            }
            catch
            {
                // either this whole tile was in the sea (but in that case the tile should have just been skipped) 
                // or all the non-sea points had insufficient data to run the model. We'll just write 0 tiles out.
                nTiles = 0;
            }
            var tileArr = new float[nTiles][];
            var ndv = _maxReader.NoDataValue.Value;
            for (int b = 0; b < nTiles; b++)
            {
                tileArr[b] = new float[nCells];
            }
            int cellNum;
            
            for (int y = 0; y < ySize; y++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    cellNum = y * xSize + x;
                    if (CellData[cellNum].Length == 0)
                    {
                        // this cell was in the sea, indicated by the special case result that the cell data is of length 0
                        // Write nodata for this location into all tiles
                        for (int b = 0; b < nTiles; b++)
                        {
                            tileArr[b][cellNum] = ndv;
                        }
                    }
                    else
                    {
                        for (int b = 0; b < nTiles; b++)
                        {
                            tileArr[b][cellNum] = CellData[cellNum][b];
                        }
                    }
                }
            }
            return tileArr;
        }
        

    }
    
}
