﻿using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MachineLearningSpectralFittingCode
{
    class Spectral_Model
    {
        public Spectral_Model(string path,
                       bool milky_Way_Reddening = true, // SHOULD BE TRUE
                       bool hPF_Mode = true,
                       ushort n_Masked_Amstrongs = 20,
                       Accelerator GPU = null)
        {
            this.Path = path;
            this.Milky_Way_Reddening = milky_Way_Reddening;
            this.HPF_Mode = hPF_Mode;
            this.N_Masked_Amstrongs = n_Masked_Amstrongs;
            this.gpu = GPU;
        }

        Accelerator gpu;

        // SPECTRA SPECIFIC VARIABLES
        #region
        // constructor variables
        public string Path { get; private set; }
        public bool Milky_Way_Reddening { get; private set; }
        public bool HPF_Mode { get; private set; }
        public ushort N_Masked_Amstrongs { get; private set; }

        // Initialisation variables
        /// <summary>
        /// DATA wavelength in OBSERVERS Frame of REFERENCE
        /// </summary>
        public Vector Wavelength { get; private set; }
        /// <summary>
        /// DATA Flux Parameter
        /// </summary>
        public Vector Flux { get; private set; }
        /// <summary>
        /// DATA ERROR PARAMETER
        /// </summary>
        public Vector Error { get; private set; }
        public float Redshift { get; private set; }
        public float[] RA_DEC { get; private set; }
        public float Velocity_Dispersion { get; private set; }
        public Vector Instrument_Resolution { get; private set; }

        public float Distance_Luminosity { get; private set; }
        public Vector Bad_Flags { get; private set; }
        public Vector Restframe_Wavelength { get; private set; }
        // !!errorNES!!
        public byte Trust_Flag { get; private set; }
        // !!errorNES!!
        public byte ObjID { get; private set; }
        // Bad Data ?? 
        // mask emission lines
        public float ebv_MW { get; private set; }
        #endregion

        // MODEL SPECIFIC VARIABLES
        #region
        int velocity_dispersion_r { get; set; }
        int fit_per_iteration_cap { get; set; }
        // Model Main Values
        /// <summary>
        /// MODEL wavelengths
        /// </summary>
        public float[] Model_wavelength;
        /// <summary>
        /// MODEL FLUXES - List of MODEL FLUXES
        /// </summary>
        public Vector Model_flux; // Becomes NORMED after Method - NormaliseSpec
        /// <summary>
        /// MODEL AGES
        /// </summary>
        public float[] Model_ages;
        /// <summary>
        /// MODEL METALICITIES
        /// </summary>
        public float[] Model_metals;
        public float[] Mass_factor;
        public int[] MatchingWavelengthMapping;


        #endregion

        // Spectrum Initialisation
        public async void InitialiseSpectraParameters(Vector Data, float Redshift, float[] RA_DEC, float Velocity_Disp, float instrument_resolution) // also include emission lines masking
        {
            bool warn = false;

        retryWave:
            try
            {
                // Set Long tasks to execute
                this.Wavelength = await Vector.AccessSliceAsync(gpu, Data, 0, 'c');  // WAVELENGTH
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryWave;
            }

        retryFlux:
            try
            {
                // Set Long tasks to execute
                this.Flux = Vector.AccessSlice(gpu, Data, 1, 'c');  // FLUX
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryFlux;
            }

        retryErr:
            try
            {
                // Set Long tasks to execute
                this.Error = await Vector.AccessSliceAsync(gpu, Data, 2, 'c');  // ERROR
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryErr;
            }


            this.Redshift = Redshift;
            this.RA_DEC = RA_DEC;
            this.Velocity_Dispersion = Velocity_Disp;
            this.Trust_Flag = 1;
            this.ObjID = 0;


        retryInstru:
            try
            {
                this.Instrument_Resolution = Vector.Fill(instrument_resolution, this.Wavelength.Value.Length);
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryInstru;
            }


            if (this.Milky_Way_Reddening)
            {
                this.ebv_MW = this.GetDustRADEC(this.RA_DEC, "ebv");
            }
            else
            {
                this.ebv_MW = 0f;
            }


        retryRestWave:
            try
            {
                this.Restframe_Wavelength = Vector.ScalarOperation(gpu, this.Wavelength, (1 + this.Redshift), "/");
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryRestWave;
            }

        // Remove Bad data from the spectrum
        // Generates the Filter Mask
        retryBadDat:
            try
            {
                float[] BadDataMask = this.GenerateDataMask(this.Flux, this.Error);

                if (BadDataMask.Contains(0f))
                {
                    // Filter Out the bad data
                    Console.WriteLine("Warning Bad Data Detected");
                }
                else
                {
                    //Console.WriteLine("Data is Fine");
                }
                // Else just proceed as the Data is Fine   
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(50);
                goto retryBadDat;
            }



        retryDistLum:
            try
            {
                // CALCULATE LUMINOSITY DISTANCE in CM
                // Two versions to Test Normal and Optimised Algo. no noticable performance difference noted so far
                this.Distance_Luminosity = UtilityMethods.Mpc2cm(await Program.cosmology.GPU_IntegrationOptiAsync(gpu, this.Redshift, 1e-8f)); //Program.cosmology.luminosity_distance(this.Redshift));
            }
            catch (Exception)
            {
                warn = true;
                await Task.Delay(100);
                goto retryDistLum;
            }

            
            // Initialise Model Section
            this.InitialiseSPModel();


            // Display warning
            if (warn)
            {
                Console.WriteLine("Warning Code Executed Slower Than Expected - Insufficient Memory");
            }

        }

        // Model Initialisation
        private void InitialiseSPModel()
        {
            this.velocity_dispersion_r = (int)(MathF.Round(this.Velocity_Dispersion / 5f) * 5f);
        }


        // SPECTRAL - SPECTRUM FUNCTIONS
        #region
        private float[] GenerateDataMask(Vector flux, Vector Error) // Also add emission lines filter
        {

            AcceleratorStream Stream = gpu.CreateStream();

            var kernelWithStream = gpu.LoadAutoGroupedKernel<Index1, ArrayView<float>, ArrayView<float>, ArrayView<float>>(GPU_GenerateDataMaskKernal);

            var buffer = gpu.Allocate<float>(flux.Value.Length);
            var buffer2 = gpu.Allocate<float>(flux.Value.Length);
            var buffer3 = gpu.Allocate<float>(Error.Value.Length);

            buffer.MemSetToZero(Stream);
            buffer2.MemSetToZero(Stream);
            buffer3.MemSetToZero(Stream);

            buffer2.CopyFrom(Stream, flux.Value, 0, 0, flux.Value.Length);
            buffer3.CopyFrom(Stream, Error.Value, 0, 0, Error.Value.Length);


            kernelWithStream(Stream, buffer.Length, buffer.View, buffer2.View, buffer3.View);

            Stream.Synchronize();

            float[] Output = buffer.GetAsArray(Stream);

            buffer.Dispose();
            buffer2.Dispose();
            buffer3.Dispose();

            Stream.Dispose();

            return Output;
        }

        // GPU KERNEL
        static void GPU_GenerateDataMaskKernal(Index1 index, ArrayView<float> OutPut, ArrayView<float> flux, ArrayView<float> error)
        {
            // False means Exclude/Bad Data, True means Good Data
            OutPut[index] = Convert.ToSingle(!(float.IsNaN(flux[index]) || float.IsInfinity(flux[index]) || (flux[index] <= 0f) || float.IsNaN(error[index]) || float.IsInfinity(error[index])));
        }

        private float GetDustRADEC(float[] RADEC, string dustmap, bool interpolate = true)
        {

            float[] l_b = Eq2Gal(
                UtilityMethods.Degree2Radians(RADEC[0]),
                UtilityMethods.Degree2Radians(RADEC[1])
                );

            return Get_SFD_dust(l_b[0], l_b[1]);
        }

        private float[] Eq2Gal(float ra, float dec)
        {

            float Cos_dec_Mult_Cos_GNEV0 = (float)(Math.Cos(dec) * Math.Cos(ra - Constants.Galactic_Northpole_Equatorial.Value[0]));

            float b = (float)Math.Asin(
                Math.Sin(dec) * Math.Sin(Constants.Galactic_Northpole_Equatorial.Value[1]) +
                Math.Cos(Constants.Galactic_Northpole_Equatorial.Value[1]) * Cos_dec_Mult_Cos_GNEV0
                );

            float l = (float)Math.Atan2(
                Math.Sin(dec) * Math.Cos(Constants.Galactic_Northpole_Equatorial.Value[1]) -
                Math.Sin(Constants.Galactic_Northpole_Equatorial.Value[1]) * Cos_dec_Mult_Cos_GNEV0,
                Math.Cos(dec) * Math.Sin(ra - Constants.Galactic_Northpole_Equatorial.Value[0])
                ) + UtilityMethods.Degree2Radians(33f);

            if (l < 0)
            {
                l += Constants.TwoPi;
            }

            l %= Constants.TwoPi;


            return new float[2] { l * Constants.Rad2DegFactor, b * Constants.Rad2DegFactor };
        }

        private float Get_SFD_dust(float logitude, float latitude, string dustmap = "ebv", bool interpolate = true)
        {
            //Console.WriteLine($"longitude {logitude} , latitude {latitude}");

            // get path to dust map

            // if var dustmap is NOT a string 
            //      - Raise Exception
            //  + ALWAYS WILL BE A STRING

            // var dml = dustmap in lowercase

            /* if dustmap is 'ebv' 'eb-b' or 'e(b-v)'
             *  + ONLY accepted variation will be 'ebv'
             * 
             *      - read in file at os.environ['FF_DIR']+'/data/SFD_dust_4096_%s.fits'
             * + TEST WHERE THIS IS TO CONVERT TO HDF5
             * 
             * + RETURN ALL OTHER TYPES AS DUSTMAP NOT FOUND
             * 
             * if dustmap = i100
             * if dustmap = x
             * if dustmap = t
             * if dustmap = mask
             * else dustmapfn = dustmap ?? would that not crash?
             * 
             * if longitude is a scalar
             *      - l = convert longitude to an array and multipy by pi/180
             * + WHICH it IS
             * if latitude is a scalar
             *      - b = convert latitude to an array and multipy by pi/180
             *      
             * if length of l and b DONT match, RAISE EXCEPTION
             *  + WILL ALWAYS MATCH SINCE l and b are floats
             * 
             * if NOT dustmapfn.contains(%s)
             *  + %s WILL ALWAYS be in the path name
             *      - f = fits.open(dustmapfn)
             *      - try : mapds = [f[0].data] then f.close()
             * + READ IN DUSTMAP in python and convert to HDF5 for this and read in PreInitialise Phase
             * 
             *      - assert that mapds[-1].shape[0] is = to mapds[-1].shape[1] 
             * + Test in Python what does means
             * 
             *      - polename = dustmapfn.split('.')[0].split('_')[-1].lower() ?? Can do a Contains or Regex test instead??
             * 
             *      - if polename is 'ngp'
             *          -> n=[1]
             *      - if sum( b > 0 ) > 0
             *          -> b = b
             *      - if polename is 'sgp'
             *          -> n = [-1]
             *          - if sum ( b < 0 ) > 0:
             *              -> b = b
             *      - else
             *          - RAISE EXCEPTION
             *      
             *      -> masks = [ones(same shape as b) converted to boolean ]
             * ELSE
             *      -> nmask = b >= 0 
             *      -> smask = !nmask
             *      
             *      masks = [nmask, smask]
             *      ns = [1,-1]
             *      
             *      mapds = []
             *     =>-> READ IN THE dustmapfn fits for 'ngp'/'sgp'
             *      try: mapds.add(f[0].data) then close(f)
             *      asstert that mapds[-1].shape[0] is EQUAL to mapds[-1].shape[1]
             *     <=->repeat for 'sgp'
             *      
             *      retvals = []
             *      for n,mapd,m in zip(ns,mapds,masks) 
             *          + CHECK THIS FUNCTIONALITY IN PYTHON TO REPLICATE
             *          npix = mapd.shape[0]
             *      
             *      
             *      
            */



            return 0f;
        }

        #endregion

        // MODELS - MODEL FUNCTIONS
        #region
        public void Fit_models_to_data()
        {
            // enumerate over each model_lib (mi, mm)?
            // only 1 model library so, index mi = 0, mm = "MaStar"


            // model_lib = Model_key = MaStar-Th
            // deltal_libs = List<double> of r_model


            // enumerate over imfs (ii)
            // only 1 imf so, foreach ii in imfs
            this.Get_Model(); //sets: model_wave_int,model_flux_int,age,metal


            // this.raw_model_wave_int = model_wave_int
            // this.raw_model_flux_int ... this.metal = metal
            //Match_data_models(); 
            NormaliseSpec();

            // Part 3 - correction from dust attenuation
            // -- LOG TIME COMPARISON -- CHECKPOINT
            if (Program.config.HPF_Mode)
            {

            }
            else // hpf only if False
            {

            }


            // Part 5
            // Gets the best model

        }

        private void Get_Model()
        {

            // M11 Models
            if (Program.config.Model_Key % 2 == 1)
            {
                //var first_file = true;
                // List of model_files = []

                // Constructs the metallicity array of models:
                //var all_metal_files = sorted(glob.glob(model_path+"*"))
                // metal_files = []
                // metal = [] #[-2.25,-1.35,-0.33,0,0.35]
                // for z in range(len(all_metal_files)):
                //.
                //.
                //.

                return;
            }
            
            // MaStar Models
            if (Program.config.Model_Key % 2 == 0)
            {
                // Gets Wavelengths from Model Data
                this.Model_wavelength = Constants.wavelength;  // Lacking 1dp precision
                // Gets Indexes of Models matching wavlengths
                this.TrimModel();

                Vector new_sres = this.DownGradeModelInvar(this.Model_wavelength, this.velocity_dispersion_r, this.Instrument_Resolution);

                List<float> model_flux = new List<float>();
                List<float> age_model = new List<float>();
                List<float> metal_model = new List<float>();

                for (int i = 0; i < Constants.t.Length; i++)
                {
                    //index i in t , t value
                    if ((Constants.t[i] < Program.config.MinMax_Model_Age[0]) || (Constants.t[i] > Program.config.MinMax_Model_Age[1]))
                    {
                        continue;
                    }

                    for (int j = 0; j < Constants.Z.Length; j++)
                    {
                        // index, Z value
                        if (Constants.Z[j] < Program.config.MinMax_Model_Metalicity[0] || Constants.Z[j] > Program.config.MinMax_Model_Metalicity[1])
                        {
                            continue;
                        }
                        if (Constants.Z[j] < -1.35f && Constants.t[i] < 1)
                        {
                            continue;
                        }

                        // TRIM DOWN THE WAVELENGTH AND THE FLUX to correspond to Data
                        float[] flux = new float[Constants.wavelength.Length];
                        for (int k = 0; k < flux.Length; k++)
                        {
                            flux[k] = Constants.fluxgrid[i, j, Constants.sidx, k];
                        }


                        // no conversion to vacuum needed, assuming models are in vacuum

                        // downgrades the model
                        if (Program.config.Downgrade_models)
                        {
                            flux = this.DownGrade(this.Model_wavelength, flux, this.velocity_dispersion_r,this.Restframe_Wavelength, this.Instrument_Resolution, new_sres);
                        }
                        

                        // Reddens the models
                        if (this.ebv_MW != 0)
                        {
                            var attenuations = unred(Constants.wavelength, -this.ebv_MW); // ebv = 0f - ebv_mw

                            try
                            {
                                model_flux.AddRange(Vector.ConsecutiveOperation(gpu, (new Vector(flux)), (new Vector(attenuations)), '*').Value);

                            }
                            catch (Exception)
                            {
                                throw new Exception("Spectral Model Run Out of Memory In Fit_Data_To_Models - Get_Models Function");
                            }

                        }
                        else
                        {
                            model_flux.AddRange(flux);
                        }
                        
                        age_model.Add(Constants.t[i]);
                        metal_model.Add(Constants.Z[j]); // In FireFly 10^{Z[j]} is used to get Metalicity in units of Solar Metalicity

                    }
                }

                this.Model_flux = new Vector(model_flux.ToArray(), Constants.wavelength.Length ); // 2D flattened Array
                this.Model_ages = age_model.ToArray();
                this.Model_metals = metal_model.ToArray();

                return;
            }


        }

        private void TrimModel()
        {

            int length_Data = this.Restframe_Wavelength.Value.Length;
            int length_Mod  = this.Model_wavelength.Length;

            if (this.Model_wavelength[0] < this.Restframe_Wavelength.Value[0] && this.Model_wavelength[^1] > this.Restframe_Wavelength.Value[^1])
            {

                float[] endVal = (from mwl in this.Model_wavelength
                                  select Math.Abs(mwl - this.Restframe_Wavelength.Value[^1])).ToArray();

                int endIdx = Array.IndexOf(endVal, endVal.Min()) + 1;


                float[] startVal = (from mwl in this.Model_wavelength[0..(endIdx+1)]
                                    select Math.Abs(mwl - this.Restframe_Wavelength.Value[0])).ToArray();

                int startIdx = Array.IndexOf(startVal, startVal.Min());


                if (endIdx - startIdx == length_Data)
                {
                    this.MatchingWavelengthMapping = Enumerable.Range(startIdx, endIdx - startIdx).ToArray();
                    return;
                }


                if (endIdx - startIdx != length_Data)
                {
                    int[] indxs = new int[length_Data];
                    indxs[0] = startIdx;
                    indxs[^1] = endIdx;
                    for (int i = 1; i < indxs.Length-1; i++)
                    {
                        float[] matchtest = (from mwl in this.Model_wavelength[0..endIdx]
                                             select Math.Abs(mwl - this.Restframe_Wavelength.Value[i])).ToArray();
                        indxs[i] = Array.IndexOf(matchtest, matchtest.Min());
                    }

                    this.MatchingWavelengthMapping = indxs;
                    return;
                }

            }

            Console.WriteLine("WARNING TRIMMODEL FUNC REACHED UNFINISHED CODE");

            return;
        }

        private Vector DownGradeModelInvar(float[] mod_wavelength, int vdisp_round, Vector r_instrument)
        {
            float[] new_sig = new float[mod_wavelength.Length];

            for (int i = 0, j = 0; i < mod_wavelength.Length; i++)
            {
                if (this.MatchingWavelengthMapping[..^1].Contains(i))
                {
                    j++;
                }

                float sig_instrument = Constants.c_kms / (r_instrument.Value[j] * Constants.sig2FWHM);
                new_sig[i] = MathF.Sqrt(MathF.Pow(vdisp_round, 2f) + MathF.Pow(sig_instrument, 2f));

            }

            return Vector.ScalarOperation(gpu, new Vector(new_sig), Constants.c_div_sig2, "^*");  // new_sres
        }

        private float[] DownGrade(float[] mod_wavelength, float[] flux, int vdisp_round, Vector rest_wavelength, Vector r_instrument, Vector new_sres)
        {

            // IF mod_wavelength < 5 -> raise an error ?? Unlikely

            bool log_wave = true;
            if ((mod_wavelength[3] - mod_wavelength[2]) - (mod_wavelength[2] - mod_wavelength[1]) < 0.000001f * (mod_wavelength[2] - mod_wavelength[1]) )
            {
                log_wave = false;
            }


            match_spectral_resolution(mod_wavelength, flux, Constants.sres, mod_wavelength, new_sres, log10:log_wave, new_log10:log_wave);
            // Call match_spectral_resolution(mod_wavelength, flux, sres, mod_wavelength, new_sres, min_sig_pix=0.0,
            // log10=log_wave, new_log10=log_wave)
            // get : new_flux, matched_sres, sigma_offset, new_mask

            return flux;
        }

        private void match_spectral_resolution(float[] wave, float[] flux, float[] sres, float[] new_sres_wave, Vector new_sres, float min_sig_pix=0f, bool no_offset=true,
            bool variable_offset = false, bool log10 = false, bool new_log10 = false) // float[flux.length] ivar and float[flux.length] mask = None 
        {
            #region
            /*
            // Checking if wave and sres are 2D? and making sure they are both 2D if true // firefly
            // + Both are 1D

            // Check if 
            //          - both wave and sres are 2D but different shapes 
            //      OR  - wave is 1D AND sres is 2D AND wave shape in x is NOT = sres shape in y
            // + Both are 1D

            // Check if
            //          - wave is 2D and wave shape NOT = flux shape
            //      OR  - wave is 1D and flux is 2D AND wave shape in x NOT = flux shape in y
            //      OR  - wave is 1D and flux is 1D AND wave shape NOT = flux shape
            // + wave and flux the same shape

            // Check if
            //          - mask in NOT NONE AND mask shape NOT = flux shape
            // + mask is NONE

            // Check if 
            //          - ivar in NOT NONE and ivar shape NOT = flux shape
            // + ivar is NONE

            // Check if
            //          - sres # dimensions > flux # dimensions
            // + sres # dims = flux # dims

            // Check if 
            //          - new_sres_wave # dimensions NOT = 1
            //      OR  - new_sres # dimensions NOT = 1
            // + new_sres_wave and new_sres are both 1D

            // Check if 
            //          - new_sres_wave shape NOT = new_sres shape
            // + new_sres_wave is same shape as new_sres
            */

            // Raise a warning if the new_sres vector will have to be 
            // extrapolated for the input wavelengths
            // Check if 
            //          - minimum of wave < new_sres_wave[0]
            //      OR  - maximum of wave > new_sres_wave[-1]
            // ---> WARNS ONLY does Nothing else??
            #endregion
            if (wave.Min() < new_sres_wave[0] || wave.Max() > new_sres_wave[^1])
            {
                Console.WriteLine("WARNING : Mapping to the new spectral resolution will require extrapolating the provided input vectors!");
            }


            // # Initialize some variables
            // var nspec = 1 if flux is 1D, else set equal to flux.shape[0]
            // var nsres = 1 if flux is 1D, else set equal to sres.shape[0]
            var dims = 1;


            // Check if
            //          - sres is 2D AND nspec NOT = nsres
            // + sres is 1D and nspec = nsres


            // spec_dim = flux # dimensions
            // sres_dim = sres # dimensions
            // sigma_offset = new float[nspec] 
            
            // 0.2s
            Spectral_resolution new_res = new Spectral_resolution(gpu, new_sres_wave, new_sres.Value, log10 = new_log10);

            // res = new float[nspec] ?? Object type? ==> new object[1]

            // 0.2s
            Spectral_resolution res = new Spectral_resolution(gpu, wave, sres, log10);
            // 18-20s
            res.match(new_res, no_offset, min_sig_pix);
            float sigma_offset = res.sig_vo;
            //Console.WriteLine($"the sig vo is : {sigma_offset}");

            // Get the kernel parameters necessary to match all spectra to the new resolution
            // Check if nsres is 1 and sres_dim is 1
            //      res[0] = spectral_resolution(wave,sres,log10=log10)
            //      res[0].match(new_res, no_offset = no_offset, min_sig_pix = min_sig_pix)
            //      sigma_offset[0] = res[0].sig_vo
            //      for loop from 1 to nspec:
            //          res[i] = res[0]
            // ELSE
            //      for loop (over i) from 0 to nsres:
            //          _wave = flatten{ wave[@i,:] } if wave is 2D else set as wave
            //          _sres = flatten{ sres[@i,:] } if sres is 2D else set as sres
            //          res[@i] = spectral_resolution(_wave,_sres,log10=log10)
            //          res[@i].match(new_res, no_offset=no_offset, min_sig_pix=min_sig_pix)
            //          sigma_offset[@i] = res[@i].sig_vo

            // # Force all the offsets to be the same, if requested - Line 996 - firefly_instrument.py
            //if (!no_offset && !variable_offset)
            //{
            //    // Assuming only 1 value for sigma offset
            //    // thus common_offset = sigma_offset
            //    // thus offset_diff = 0f
            //    // thus Method res.offset_GaussianKernelDifference(0) returns None
            //}

            // FLUX DEPENDENT SECTION
            float[] out_flux = flux;
            // out_ivar = None if ivar is None else np.ma.MaskedArray(ivar.copy())
            // noise = None if ivar is None else np.ma.sqrt(1.0/out_ivar)
            float[] out_sres = sres;
            // mask = None
            bool[] mask = Enumerable.Repeat<bool>(false, flux.Length).ToArray();
            bool[] out_mask = mask;

            // Assume nspec == 1 and spec_dim == 1 === dim == 1    #01
            int[] indx = (from sigpd in res.sig_pd
                          where sigpd > min_sig_pix
                          select Array.IndexOf(res.sig_pd, sigpd)).ToArray();
            // ivar is None                                         #02

            int len = indx.Length;
            if (len > 0)
            {
                float[] selectedflux = new float[len];
                float[] selectedsig = new float[len];
                for (int i = 0; i < len; i++)
                {
                    selectedflux[i] = flux[indx[i]];
                    selectedsig[i] = res.sig_pd[indx[i]];
                }
                float[] selected_outflux = convolution_variable_sigma(selectedflux, selectedsig);
                for (int i = 0; i < len; i++)
                {
                    out_flux[indx[i]] = selected_outflux[i]; 
                }
            }
            else
            {
                out_flux = convolution_variable_sigma(flux, res.sig_pd);
            }

            // thus ignor else                                      #02

            float[] adjusted = res.adjusted_resolution(indx);
            if (len > 0f)
            {
                for (int i = 0; i < indx.Length; i++)
                {
                    out_sres[indx[i]] = adjusted[i];
                }
            }
            else
            {
                out_sres = adjusted;
            }

            if (res.sig_mask.Contains(true) || mask.Contains(true))
            {
                for (int i = 0; i < mask.Length; i++)
                {
                    out_mask[i] = res.sig_mask[i] || mask[i];
                }
            }
            else
            {
                out_mask = mask;
            }
            //thus ignore else                                       #01


        }

        private float[] convolution_variable_sigma(float[] y, float[] sigma) //ye= None, integral= False) 
        {

            return new float[0];
        }


        private void Match_data_models()
        {
            // Get model_flux and Data_flux




            /*
             * sets the
             * wave
             * model_flux_raw
             * 
             * outputs the 
             * data_flux
             * error_flux
             */
        }


        private void NormaliseSpec()
        {
            float data_norm = UtilityMethods.Median(this.Flux.Value);                 
            int num_mods = this.Model_flux.Value.Length / this.Model_flux.Columns;     // 198
            
            float[] model_norm = new float[num_mods];                                 
            float[] mass_factor = new float[num_mods];                                 


            for (int i = 0; i < num_mods; i++)
            {
                model_norm[i] = UtilityMethods.Median(this.Model_flux.Value[(i*this.Model_flux.Columns)..((i+1) * this.Model_flux.Columns)]); 
                mass_factor[i] = data_norm / model_norm[i];                            
            }

            // OVER-WRITES MODEL FLUX WITH THE NORMALISED MODEL FLUX
            this.Model_flux = Vector.ConsecutiveCompoundOperation2D(gpu, this.Model_flux, new Vector(model_norm), data_norm, "**");
            this.Mass_factor = mass_factor;

        }

        private float[] unred(float[] wavelength, float ebv_mw)
        {


            return wavelength;
        }


        #endregion


        // Test CODE
        public float CalculateChiSqu(int model)
        {
            // Get length of Data
            var length = this.Restframe_Wavelength.Value.Length;

            // Finds the closest start value
            float Closest_Start_Val = this.Model_wavelength.OrderBy(n => Math.Abs(this.Restframe_Wavelength.Value[0] - n)).First();

            // Gets Index of Closest Value
            int idx_closest = Array.IndexOf(this.Model_wavelength, Closest_Start_Val);

            // Select the Model Flux for the Model
            float[] model_flux = this.Model_flux.Value[((model * this.Model_flux.Columns) + idx_closest)..((model * this.Model_flux.Columns) + idx_closest + length)];

            // Get the Length
            //int length = this.Model_flux.Columns;
            //float[] model_flux = this.Model_flux.Value[(model * length)..((model + 1) * length)];

            float sum = 0f;
            for (int j = 0; j < model_flux.Length; j++)
            {
                sum += MathF.Pow(((model_flux[j] - this.Flux.Value[j]) / this.Error.Value[j]), 2f);
            }

            return sum;
        }


        // EXPERIMENTAL CODE DONT USE YET
        public float[] CalculateChiSquVec()
        {
            int length_Data = this.Restframe_Wavelength.Value.Length;
            int length_Mod = this.Model_wavelength.Length;
            int models = length_Data / length_Mod;

            float[] endVal = (from mwl in this.Model_wavelength
                              select Math.Abs(mwl - this.Restframe_Wavelength.Value[^1])).ToArray();

            int endIdx = Array.IndexOf(endVal, endVal.Min()) + 1;


            float[] startVal = (from mwl in this.Model_wavelength[0..(endIdx + 1)]
                                select Math.Abs(mwl - this.Restframe_Wavelength.Value[0])).ToArray();

            int startIdx = Array.IndexOf(startVal, startVal.Min());

            int model_len = endIdx - startIdx;

            List<float> new_mod_flux = new List<float>();
            for (int i = 0; i < models; i++)
            {

                float[] vals = this.Model_flux.Value[(i * length_Mod + startIdx)..(i * length_Mod + endIdx)];
                new_mod_flux.AddRange(vals);
            }


            AcceleratorStream Stream = gpu.CreateStream();

            var kernelWithStream = gpu.LoadAutoGroupedKernel<Index1, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int>(CalcChiKernel);

            var buffer = gpu.Allocate<float>(new_mod_flux.Count); // Output
            var buffer2 = gpu.Allocate<float>(new_mod_flux.Count); // Input
            var buffer3 = gpu.Allocate<float>(model_len); // Input
            var buffer4 = gpu.Allocate<float>(model_len); // Input

            buffer.MemSetToZero(Stream);
            buffer2.MemSetToZero(Stream);
            buffer3.MemSetToZero(Stream);
            buffer4.MemSetToZero(Stream);

            buffer2.CopyFrom(Stream, new_mod_flux.ToArray(), 0, 0, new_mod_flux.Count);
            buffer3.CopyFrom(Stream, this.Flux.Value, 0, 0, this.Flux.Value.Length);
            buffer4.CopyFrom(Stream, this.Error.Value, 0, 0, this.Error.Value.Length);



            kernelWithStream(Stream, buffer.Length, buffer.View, buffer2.View, buffer3.View, buffer4.View , model_len);

            Stream.Synchronize();

            float[] Output = buffer.GetAsArray(Stream);

            buffer.Dispose();
            buffer2.Dispose();
            buffer3.Dispose();
            buffer4.Dispose();

            Stream.Dispose();

            float[] chis = new float[models];
            for (int i = 0; i < models; i++)
            {
                chis[i] = Output[(i * model_len)..((i + 1) * model_len)].Sum();
            }


            return chis;
        }
        // Kernel
        static void CalcChiKernel(Index1 index, ArrayView<float> Output, ArrayView<float> InputMod_flux, ArrayView<float> InputDat_flux, ArrayView<float> InputDat_err, int model_len)
        {
            Output[index] = XMath.Pow((InputMod_flux[index] - InputDat_flux[index % model_len]) / InputDat_err[index % model_len], 2f);
        }




    }



}
