	using System;
	using System.Threading;
	using System.Xml;
	using System.Text;
	using System.IO;
	using System.Drawing;
	using System.Collections.Generic;
	using System.Web.Services;

	using Clayster.Library.RaspberryPi;
	using Clayster.Library.RaspberryPi.Devices.ADC;
	using Clayster.Library.RaspberryPi.Devices.Temperature;
	using SimpleHttpServer;











	namespace WaterSystem
	{
		 class MainClass
		{
		//Client (Plant watering device)


			//Directives
			private static int waterLevelMax = 0;
			private static int waterLevelMin = 0;
			private static int acceptableMarginOfError = 10;
			private static int waterFreq = 3;
			private enum WaterState {ok, busy, dry, finished};
			private static WaterState waterState = WaterState.ok;
			private static int hour = 9;
			private static DateTime waterTime = new DateTime(DateTime.Now.Year,DateTime.Now.Month,DateTime.Now.Day,hour,0,0);


			//DEVICES
			private static DigitalOutput executionLed = new DigitalOutput (23, true);
			private static DigitalOutput waterPump = new DigitalOutput (6, false);
			private static I2C i2cBus = new I2C (3, 2, 400000);
			private static AD799x adc = new AD799x (0, true, true, false, false, i2cBus);
			//private static TexasInstrumentsTMP102 tmp102 = new TexasInstrumentsTMP102 (0, i2cBus);

			//Data Storage
			private static int[] waterHistory = new int[3600];
			private static int[] lightHistory = new int[3600];
			private static int[] tempHistory = new int[3600];




			private static object waterPumpAccess = new object ();





			//MESSAGES


			// Through these messages the device is given its directives for when to water. 
			// The owner of the device inserts the moisture sensor and waits 5 minutes for the sensor value to stabilize (will be automated later)
			// Then clicks the setLow Moisture level via a website
			// Then waters the soil to a prefered level and waits 5 minutes
			//	Then clicks the setHigh Moisture Level via the website

			private static bool SetLow(){
				waterLevelMin = waterHistory[currentTime()];
				Console.WriteLine(" Min level: {0}", waterLevelMin);

				return true;
			}

			private static bool SetHigh(){
				waterLevelMax = waterHistory[currentTime()];
				waterState = WaterState.ok;
				Console.WriteLine(" Max level: {0}", waterLevelMax);
				return true;
			}

			//For when water tank runs dry and one has reset the system
			private static bool SetRefilled ()
			{
				waterState = WaterState.ok;
				 return true;
			}

			//For when the user wants a different amount of days between watering than 3
			private static bool SetNewWaterFreq (int newFreq)
			{
				waterFreq = newFreq;
				return true;
			}


			//SCHEDULER

			public static void Main ()
			{
				Server srv = new Server(SetHigh, SetLow, SetRefilled, SetNewWaterFreq, 8080);
				Thread thread = new Thread(new ThreadStart(srv.listen));
	            thread.Start();
				Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
				{
					e.Cancel = true;
					executionLed.Low ();

				};
				///  SCHEDULER  ///
				while(executionLed.Value)
				{

					getState ();

					 if (waterState == WaterState.ok 
					    && waterLevelMax != 0 
					    && DateTime.Now.Day == waterTime.Day && DateTime.Now.Minute > waterTime.Minute  
					    && DateTime.Now.Hour == waterTime.Hour
					    && waterHistory[DateTime.Now.Hour * 60 + DateTime.Now.Minute] < waterLevelMax - acceptableMarginOfError) 
					{

						waterState = WaterState.busy;
						new Thread (() => water (waterLevelMax)).Start ();
						waterTime = waterTime.AddDays(waterFreq); 
					} 

					else if(waterState == WaterState.ok && averageMoist() < waterLevelMin - acceptableMarginOfError && waterLevelMax != 0 )
					{

						waterState = WaterState.busy;
						new Thread (() => water (waterLevelMin)).Start ();
					} 
					// Sleep one hour
					System.Threading.Thread.Sleep(1000);
					
				}
				executionLed.Dispose ();
				waterPump.Low ();

			}


			// HELPERS
			static void getState()
			{
				waterHistory[currentTime ()] = (waterHistory[currentTime ()]+ adc.ReadRegistersBinary () [0])/2; // high moist values give lower digital output than low moist values,
				// thus, by simply setting the values as negative, comparing a high moisture value is in fact greater than a low moisture value (-4000 < -400 )
				//tempHistory [DateTime.Now.Minute] = (int) tmp102.ReadHighTemperatureC ();
				lightHistory[currentTime ()] = adc.ReadRegistersBinary () [1];

				Console.WriteLine(waterHistory[currentTime ()]);
			}


			private static void AcceptableMarginOfError(int temp)
			{
				acceptableMarginOfError = temp;
			}

			private static int currentTime ()
			{ 
				return DateTime.Now.Hour * 60 + DateTime.Now.Minute;
			}

			//ACTIONS

		
		


			// GATHER DATA

				

			private static int averageLight()
			{
				int light = 0; int size = 60;
				for (int i = 0; i < size; i++) {
					light += lightHistory [currentTime () - i];
				}
				light /= size;
				return light;
			}

			private static int averageHeat()
			{

				int temp = 0; int size = 60;
				for (int i = 0; i < size; i++) {
					temp += lightHistory [currentTime () - i];
				}
				temp /= size;
				return temp;

			}

			private static int averageMoist()
			{


				int temp = 0; int size = 1;
				for (int i = 0; i < size; i++) {
				temp += waterHistory[currentTime() - i];
				}
				temp /= size;
				return temp;

			}
		
				

			// ACT UPON DATA

			static void water (int waterLevelGoal)
			{ // 

				//the watering happens in steps, taking into account the delay between watering and the moisture level increasing.
				int waterInterval = 10; // seconds
				int waitInterval = 2;

				lock (waterPumpAccess) {

					int waterLevelInitial = waterHistory [currentTime ()];
					Console.WriteLine ("Watering for level " + waterLevelGoal);
					DateTime start =  DateTime.Now;

					for (int i = 0; i < 20; i++) {

						// water and wait
						waterPump.High ();
						System.Threading.Thread.Sleep (1000*waterInterval);
						waterPump.Low ();
						System.Threading.Thread.Sleep (1000*waitInterval);


						// Check to see if waterLevel is within an appropriate range
						Console.WriteLine (waterHistory [currentTime ()] - waterLevelInitial);

						//if two minutes have passed and the waterLevel has not risen more than 5, whatever 5 means, pump is either broken, or the water is empty, or the battery is out  
						if ( waterHistory [DateTime.Now.Hour * 60 + DateTime.Now.Minute] - waterLevelInitial < 10 ) {
							//send alarm
							waterState = WaterState.dry;
							Console.WriteLine ("Dry");
							return;
						}
						if (waterHistory [currentTime ()] > waterLevelGoal - acceptableMarginOfError)
						{
							if(waterHistory [currentTime ()] > waterLevelGoal)
								waterInterval--;
							Console.WriteLine ("Watering complete " + DateTime.Now);
							waterState = WaterState.ok;
							return;
						}

						if(!executionLed.Value)
							return;


					}

					//increase the amount of watering since the loop finished without reaching the waterLevelGoal 
						
					return;

				}
			}



		
		}
	}
