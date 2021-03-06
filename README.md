﻿## Temperature Suitability for Pf Malaria

This is a re-implementation of the model described in: 
Air temperature suitability for Plasmodium falciparum malaria transmission in Africa 2000-2012: a high-resolution spatiotemporal prediction (Weiss et al, 2014, DOI: 10.1186/1475-2875-13-171)

The model described in the above-referenced paper was written in IDL with the core numerical calculation code written in C. The IDL code 
dealt with reading and writing the data image files, and created temporary data files for the C code to use.
This implementation is written in C# throughout and all I/O will be handled within the program. 

At present the code is still in development, although it is functional and has been used to create global 30-arcsecond monthly series of temperature suitability for the entire MODIS period of record.

## Implementation notes

Note that the calculation process happens the "opposite way round" to the original Weiss code (refer to the referenced paper for a description of that code). 

The original code would iterate through all the time-slices of the model, and at each point would model a single mosquito cohort emerging at that point and 
then tracking forward through its entire lifespan, adding a contribution to all timeslices for which it lives.
This approach had two drawbacks in efficiency. 

Firstly, the 2-hourly temperature series (or rather the degree-day series derived from this) 
had to be generated and stored in its entirety for the whole run period. This requires more memory and presumably for this 
reason the model at each cell was run one month at a time. However this caused a secondary burden in that each run requires an additional month of "spin-up" time
and so the overall computation burden was doubled as each month was run twice. 

The second drawback was that this approach of repeatedly going 
through the degree-day and output arrays by 372 steps, moving the start point forwards by one step each time, and breaking 
out of that loop if the surviving fraction reached zero, is bound to be much less amenable to CPU caching as much of the data 
from the arrays will be loaded to cache then discarded.
  
This new implementation improves performance by taking a more linear approach. It tracks an overall population of mosquitoes living
at a cell. This population is made up of all cohorts currently living. We then simply iterate through all (2 hour) timeslices once from 
start to finish of the overall runtime (the MODIS period of record, from 2000 to e.g. 2017). 
At each slice we interpolate a temperature value (from the splined daily data and the time-of-day), calculate a surviving fraction, and 
apply this to all cohorts of the population, whilst summarising the overall contribution of the current population 
at each step. We do not need to store the interpolated temperatures or survival rates. 

Excluding the spin-up issue, the overall number of loops is the same either way but this approach is more 
cache-friendly: the only array accesses in the innermost loop are to the cohorts of the population itself and these are all 
accessed in sequential order each time so the cache hit should be close to 100%. 
Moreover we only have to initialise the model once, and then run it for the entire time period, rather than spinning up the 
model for a month prior to running every month. Finally, we parallelise the computation via multithreading, at the level of individual cells, rather than multiprocessing, minimising overheads.

## Running
The model was written in C# but was run on Linux servers using the Mono .NET runtime. Instructions for compiling and using pre-compiled GDAL binaries can be found in linux_compilation.txt.

Configuration of the various parameters is via an XML file. To generate a template XML file, first run the application passing a folder path as an argument. A template file will be written to this folder which you should edit to configure the file paths and model parameters.

The model is run with gapfilled MODIS Land Surface Temperature data for day and night temperatures, with filenames in the internal MAP mastergrid naming syntax. Briefly this consists of 6 dot-separated tokens of which the 2nd and 3rd tokens respectively give the year and julian day represented by the file; the model uses these values to construct the timeline of the data. The gapfilled data may be provided by MAP if you wish to run the model with your own data - please apply to harry.gibson@bdi.ox.ac.uk.
