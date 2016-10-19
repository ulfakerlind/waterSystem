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


using Clayster.Library.Internet;
using Clayster.Library.Internet.HTML;
using Clayster.Library.Internet.MIME;
using Clayster.Library.Internet.JSON;
using Clayster.Library.Internet.Semantic.Turtle;
using Clayster.Library.Internet.Semantic.Rdf;
using Clayster.Library.IoT;
using Clayster.Library.IoT.SensorData;
using Clayster.Library.Math;
using Clayster.Library.Internet.HTTP;
using Clayster.Library.Internet.HTTP.ServerSideAuthentication;
using Clayster.Library.EventLog;
using Clayster.Library.EventLog.EventSinks.Misc;
using Clayster.Library.Data;











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


		//
		private static bool[] digitalOutputs = new bool[8]; 
		private static Thread alarmThread = null;
		// Object database proxy
		internal static ObjectDatabase db;

		// Login credentials
		private static LoginCredentials credentials;

		// Output states
		private static State state;


		//MESSAGES

		// Currently called from HttpPostSet , only setLow and setHigh are implemented

		// Through these messages the device is given its directives for when to water. 
		// The owner of the device inserts the moisture sensor and waits 5 minutes for the sensor value to stabilize (will be automated later)
		// Then clicks the setLow Moisture level via a website
		// Then waters the soil to a prefered level and waits 5 minutes
		//	Then clicks the setHigh Moisture Level via the website

		private static void SetLow(){
			waterLevelMin = averageMoist();
			Console.WriteLine(" Min level: {0}", waterLevelMin);
		}

		private static void SetHigh(){
			waterLevelMax = averageMoist ();
			waterState = WaterState.ok;
			Console.WriteLine(" Max level: {0}", waterLevelMax);

		}

		//For when water tank runs dry and one has reset the system
		private static void SetRefilled ()
		{
			waterState = WaterState.ok;
		}

		//For when the user wants a different amount of days between watering than 3
		private static void SetNewWaterFreq (int newFreq)
		{
			waterFreq = newFreq;
		}


		//SCHEDULER

		public static void Main ()
		{



				//Old Code
			HttpSocketClient.RegisterHttpProxyUse (false, false);

			DB.BackupConnectionString = "Data Source=actuator.db;Version=3;";
			DB.BackupProviderName = "Clayster.Library.Data.Providers.SQLiteServer.SQLiteServerProvider";
			db = DB.GetDatabaseProxy ("TheActuator");

			HttpServer HttpServer = new HttpServer (80, 10, true, true, 1);
			Log.Information ("HTTP Server receiving requests on port " + HttpServer.Port.ToString ());

			HttpServer.RegisterAuthenticationMethod (new DigestAuthentication ("The Actuator Realm", GetDigestUserPasswordHash));
			HttpServer.RegisterAuthenticationMethod (new SessionAuthentication ());

			credentials = LoginCredentials.LoadCredentials ();
			if (credentials == null) {

				credentials = new LoginCredentials ();
				credentials.UserName = "Admin";
				credentials.PasswordHash = CalcHash ("Admin", "Password");
				credentials.SaveNew ();
				
			}

			state = State.LoadState ();
			if (state == null)
			{
				state = new State ();
				state.SaveNew ();
			}


			Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) => {
				e.Cancel = true;
				executionLed.Low ();
			};

			HttpServer.Register ("/", HttpGetRoot, HttpPostRoot, false);							// Synchronous, no authentication
			HttpServer.Register ("/credentials", HttpGetCredentials, HttpPostCredentials, false);	// Synchronous, no authentication
			HttpServer.Register ("/set", HttpGetSet, HttpPostSet, true);
			HttpServer.Register (new WebServiceAPI (), true);// Synchronous, http authentication




			// Initializing for testing only
			for (int i = 0; i< 3600; i++) {
				waterHistory[i] = -800;
			}

			waterLevelMin = -800;

			waterLevelMax = -400;

			waterTime = DateTime.Now;
			waterTime = waterTime.AddMinutes (2);



			///  SCHEDULER  ///
			while(executionLed.Value)
			{

				getState ();

				 if (waterState == WaterState.ok && waterLevelMax != 0 && DateTime.Now.Day == waterTime.Day && DateTime.Now.Minute > waterTime.Minute  && DateTime.Now.Hour == waterTime.Hour && waterHistory[DateTime.Now.Hour * 60 + DateTime.Now.Minute] < waterLevelMax - acceptableMarginOfError) 
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


					// Old Code
					RemoveOldSessions ();

					// Sleep one hour
					System.Threading.Thread.Sleep(60*60*1000);
				
			}


			// Old Code
				Log.Information ("Terminating application.");
				Log.Flush ();
				Log.Terminate ();

				HttpServer.Dispose ();
				executionLed.Dispose ();
			

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

			static void getState()
		{
			waterHistory[currentTime ()] = (-1) * adc.ReadRegistersBinary () [0]; // high moist values give lower digital output than low moist values,
			// thus, by simply setting the values as negative, comparing a high moisture value is in fact greater than a low moisture value (-4000 < -400 )
			//tempHistory [DateTime.Now.Minute] = (int) tmp102.ReadHighTemperatureC ();
			lightHistory[currentTime ()] = adc.ReadRegistersBinary () [1];

			Console.WriteLine(waterHistory[DateTime.Now.Hour * 60 + DateTime.Now.Minute]);
		}

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
				temp += waterHistory [currentTime () - i ];
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



	
		// Old Code
		private static void HttpGetRoot (HttpServerResponse resp, HttpServerRequest req)
		{
			string SessionId = req.Header.GetCookie ("SessionId");

			resp.ContentType = "text/html";
			resp.Encoding = System.Text.Encoding.UTF8;
			resp.ReturnCode = HttpStatusCode.Successful_OK;

			if (CheckSession (SessionId))
			{
				resp.Write ("<html><head><title>Actuator</title></head><body><h1>Welcome to Actuator</h1><p>Below, choose what you want to do.</p><ul>");
				resp.Write ("<li><a href='/credentials'>Update login credentials.</a></li>");
				resp.Write ("<li><a href='/set'>Control Outputs</a></li>");
				resp.Write ("<li>View Output States</li><ul>");
				resp.Write ("<li><a href='/xml?Momentary=1'>View data as XML using REST</a></li>");
				resp.Write ("<li><a href='/json?Momentary=1'>View data as JSON using REST</a></li>");
				resp.Write ("<li><a href='/turtle?Momentary=1'>View data as TURTLE using REST</a></li>");
				resp.Write ("<li><a href='/rdf?Momentary=1'>View data as RDF using REST</a></li></ul>");
				resp.Write ("</ul></body></html>");

			} else
				OutputLoginForm (resp, string.Empty);
		}

		private static void OutputLoginForm (HttpServerResponse resp, string Message)
		{
			resp.Write ("<html><head><title>Actuator</title></head><body><form method='POST' action='/' target='_self' autocomplete='true'>");
			resp.Write (Message);
			resp.Write ("<h1>Login</h1><p><label for='UserName'>User Name:</label><br/><input type='text' name='UserName'/></p>");
			resp.Write ("<p><label for='Password'>Password:</label><br/><input type='password' name='Password'/></p>");
			resp.Write ("<p><input type='submit' value='Login'/></p></form></body></html>");
		}

		private static void HttpPostRoot (HttpServerResponse resp, HttpServerRequest req)
		{
			FormParameters Parameters = req.Data as FormParameters;
			if (Parameters == null)
				throw new HttpException (HttpStatusCode.ClientError_BadRequest);

			string UserName = Parameters ["UserName"];
			string Password = Parameters ["Password"];
			string Hash;
			object AuthorizationObject;

			GetDigestUserPasswordHash (UserName, out Hash, out  AuthorizationObject);

			if (AuthorizationObject == null || Hash != CalcHash (UserName, Password))
			{
				resp.ContentType = "text/html";
				resp.Encoding = System.Text.Encoding.UTF8;
				resp.ReturnCode = HttpStatusCode.Successful_OK;

				Log.Warning ("Invalid login attempt.", EventLevel.Minor, UserName, req.ClientAddress);
				OutputLoginForm (resp, "<p>The login was incorrect. Either the user name or the password was incorrect. Please try again.</p>");
			} else
			{
				Log.Information ("User logged in.", EventLevel.Minor, UserName, req.ClientAddress);

				string SessionId = CreateSessionId (UserName);
				resp.SetCookie ("SessionId", SessionId, "/");
				resp.ReturnCode = HttpStatusCode.Redirection_SeeOther;
				resp.AddHeader ("Location", "/");
				resp.SendResponse ();
				// PRG pattern, to avoid problems with post back warnings in the browser: http://en.wikipedia.org/wiki/Post/Redirect/Get
			}
		}

		private static void HttpGetCredentials (HttpServerResponse resp, HttpServerRequest req)
		{
			string SessionId = req.Header.GetCookie ("SessionId");
			if (!CheckSession (SessionId))
				throw new HttpTemporaryRedirectException ("/");

			resp.ContentType = "text/html";
			resp.Encoding = System.Text.Encoding.UTF8;
			resp.ReturnCode = HttpStatusCode.Successful_OK;

			OutputCredentialsForm (resp, string.Empty);
		}

		private static void OutputCredentialsForm (HttpServerResponse resp, string Message)
		{
			resp.Write ("<html><head><title>Actuator</title></head><body><form method='POST' action='/credentials' target='_self' autocomplete='true'>");
			resp.Write (Message);
			resp.Write ("<h1>Update Login Credentials</h1><p><label for='UserName'>User Name:</label><br/><input type='text' name='UserName'/></p>");
			resp.Write ("<p><label for='Password'>Password:</label><br/><input type='password' name='Password'/></p>");
			resp.Write ("<p><label for='NewUserName'>New User Name:</label><br/><input type='text' name='NewUserName'/></p>");
			resp.Write ("<p><label for='NewPassword1'>New Password:</label><br/><input type='password' name='NewPassword1'/></p>");
			resp.Write ("<p><label for='NewPassword2'>New Password again:</label><br/><input type='password' name='NewPassword2'/></p>");
			resp.Write ("<p><input type='submit' value='Update'/></p></form></body></html>");
		}

		private static void HttpPostCredentials (HttpServerResponse resp, HttpServerRequest req)
		{
			string SessionId = req.Header.GetCookie ("SessionId");
			if (!CheckSession (SessionId))
				throw new HttpTemporaryRedirectException ("/");

			FormParameters Parameters = req.Data as FormParameters;
			if (Parameters == null)
				throw new HttpException (HttpStatusCode.ClientError_BadRequest);

			resp.ContentType = "text/html";
			resp.Encoding = System.Text.Encoding.UTF8;
			resp.ReturnCode = HttpStatusCode.Successful_OK;

			string UserName = Parameters ["UserName"];
			string Password = Parameters ["Password"];
			string NewUserName = Parameters ["NewUserName"];
			string NewPassword1 = Parameters ["NewPassword1"];
			string NewPassword2 = Parameters ["NewPassword2"];

			string Hash;
			object AuthorizationObject;

			GetDigestUserPasswordHash (UserName, out Hash, out  AuthorizationObject);

			if (AuthorizationObject == null || Hash != CalcHash (UserName, Password))
			{
				Log.Warning ("Invalid attempt to change login credentials.", EventLevel.Minor, UserName, req.ClientAddress);
				OutputCredentialsForm (resp, "<p>Login credentials provided were not correct. Please try again.</p>");
			} else if (NewPassword1 != NewPassword2)
			{
				OutputCredentialsForm (resp, "<p>The new password was not entered correctly. Please provide the same new password twice.</p>");
			} else if (string.IsNullOrEmpty (UserName) || string.IsNullOrEmpty (NewPassword1))
			{
				OutputCredentialsForm (resp, "<p>Please provide a non-empty user name and password.</p>");
			} else if (UserName.Length > DB.ShortStringClipLength)
			{
				OutputCredentialsForm (resp, "<p>The new user name was too long.</p>");
			} else
			{
				Log.Information ("Login credentials changed.", EventLevel.Minor, UserName, req.ClientAddress);

				credentials.UserName = NewUserName;
				credentials.PasswordHash = CalcHash (NewUserName, NewPassword1);
				credentials.UpdateIfModified ();

				resp.ReturnCode = HttpStatusCode.Redirection_SeeOther;
				resp.AddHeader ("Location", "/");
				resp.SendResponse ();
				// PRG pattern, to avoid problems with post back warnings in the browser: http://en.wikipedia.org/wiki/Post/Redirect/Get
			}
		}

		private static void GetDigestUserPasswordHash (string UserName, out string PasswordHash, out object AuthorizationObject)
		{
			lock (credentials)
			{
				if (UserName == credentials.UserName)
				{
					PasswordHash = credentials.PasswordHash;
					AuthorizationObject = UserName;
				} else
				{
					PasswordHash = null;
					AuthorizationObject = null;
				}
			}
		}

		private static string CalcHash (string UserName, string Password)
		{
			return Clayster.Library.Math.ExpressionNodes.Functions.Security.MD5.CalcHash (
				string.Format ("{0}:The Actuator Realm:{1}", UserName, Password));
		}

		private static Dictionary<string,KeyValuePair<DateTime, string>> lastAccessBySessionId = new Dictionary<string, KeyValuePair<DateTime, string>> ();
		private static SortedDictionary<DateTime,string> sessionIdByLastAccess = new SortedDictionary<DateTime, string> ();
		private static readonly TimeSpan sessionTimeout = new TimeSpan (0, 2, 0);	// 2 minutes session timeout.
		private static Random gen = new Random ();

		private static bool CheckSession (string SessionId)
		{
			string UserName;
			return CheckSession (SessionId, out UserName);
		}

		internal static bool CheckSession (string SessionId, out string UserName)
		{
			KeyValuePair<DateTime, string> Pair;
			DateTime TP;
			DateTime Now;

			UserName = null;

			lock (lastAccessBySessionId)
			{
				if (!lastAccessBySessionId.TryGetValue (SessionId, out Pair))
					return false;

				TP = Pair.Key;
				Now = DateTime.Now;

				if (Now - TP > sessionTimeout)
				{
					lastAccessBySessionId.Remove (SessionId);
					sessionIdByLastAccess.Remove (TP);
					return false;
				}

				sessionIdByLastAccess.Remove (TP);
				while (sessionIdByLastAccess.ContainsKey (Now))
					Now = Now.AddTicks (gen.Next (1, 10));

				sessionIdByLastAccess [Now] = SessionId;
				UserName = Pair.Value;
				lastAccessBySessionId [SessionId] = new KeyValuePair<DateTime, string> (Now, UserName);
			}

			return true;
		}

		private static string CreateSessionId (string UserName)
		{
			string SessionId = Guid.NewGuid ().ToString ();
			DateTime Now = DateTime.Now;

			lock (lastAccessBySessionId)
			{
				while (sessionIdByLastAccess.ContainsKey (Now))
					Now = Now.AddTicks (gen.Next (1, 10));

				sessionIdByLastAccess [Now] = SessionId;
				lastAccessBySessionId [SessionId] = new KeyValuePair<DateTime, string> (Now, UserName);
			}

			return SessionId;
		}

		private static void RemoveOldSessions ()
		{
			Dictionary<string,KeyValuePair<DateTime, string>> ToRemove = null;
			DateTime OlderThan = DateTime.Now.Subtract (sessionTimeout);
			KeyValuePair<DateTime, string> Pair2;
			string UserName;

			lock (lastAccessBySessionId)
			{
				foreach (KeyValuePair<DateTime,string>Pair in sessionIdByLastAccess)
				{
					if (Pair.Key <= OlderThan)
					{
						if (ToRemove == null)
							ToRemove = new Dictionary<string, KeyValuePair<DateTime, string>> ();

						if (lastAccessBySessionId.TryGetValue (Pair.Value, out Pair2))
							UserName = Pair2.Value;
						else
							UserName = string.Empty;

						ToRemove [Pair.Value] = new KeyValuePair<DateTime, string> (Pair.Key, UserName);
					} else
						break;
				}

				if (ToRemove != null)
				{
					foreach (KeyValuePair<string,KeyValuePair<DateTime, string>>Pair in ToRemove)
					{
						lastAccessBySessionId.Remove (Pair.Key);
						sessionIdByLastAccess.Remove (Pair.Value.Key);

						Log.Information ("User session closed.", EventLevel.Minor, Pair.Value.Value);
					}
				}
			}
		}

		private static void HttpGetSet (HttpServerResponse resp, HttpServerRequest req)
		{
			string s;
			int i;
			bool b;





			foreach (KeyValuePair<string,string> Query in req.Query)
			{
				if (!XmlUtilities.TryParseBoolean (Query.Value, out b))
					continue;

				s = Query.Key.ToLower ();


				if (s.StartsWith ("do") && int.TryParse (s.Substring (2), out i) && i == 1)
				{
					SetHigh();
					state.SetDO (i, b);

				}

				else if (s.StartsWith ("do") && int.TryParse (s.Substring (2), out i) && i==2)
				{

					state.SetDO (i, b);
				}

				else if (s.StartsWith ("do") && int.TryParse (s.Substring (2), out i) && i==3)
				{
					SetRefilled ();
					state.SetDO (i, b);
				}
			}

			state.UpdateIfModified ();

			resp.ContentType = "text/html";
			resp.Encoding = System.Text.Encoding.UTF8;
			resp.ReturnCode = HttpStatusCode.Successful_OK;

			resp.Write ("<html><head><title>Actuator</title></head><body><h1>Control Actuator Outputs</h1>");
			resp.Write ("<form method='POST' action='/set' target='_self'><p>");

			for (i = 0; i < 8; i++)
			{
				resp.Write ("<input type='checkbox' name='do");
				resp.Write ((i + 1).ToString ());
				resp.Write ("'");
				if (digitalOutputs [i])
					resp.Write (" checked='checked'");
				resp.Write ("/> Digital Output ");
				resp.Write ((i + 1).ToString ());
				resp.Write ("<br/>");
			}

			resp.Write ("<input type='checkbox' name='alarm'");
			if (alarmThread != null)
				resp.Write (" checked='checked'");
			resp.Write ("/> Alarm</p>");
			resp.Write ("<p><input type='submit' value='Set'/></p></form></body></html>");
		}

		private static void HttpPostSet (HttpServerResponse resp, HttpServerRequest req)
		{
				if(waterLevelMin ==  0)
				SetLow ();

			else
				SetHigh ();

			FormParameters Parameters = req.Data as FormParameters;
			if (Parameters == null)
				throw new HttpException (HttpStatusCode.ClientError_BadRequest);

			int i;
			bool b;

			for (i = 0; i < 8; i++)
			{
				if (XmlUtilities.TryParseBoolean (Parameters ["do" + (i + 1).ToString ()], out b) && b)
				{
					digitalOutputs [i]= true;
					state.SetDO (i + 1, true);
				} else
				{
					digitalOutputs [i] = false;	// Unchecked checkboxes are not reported back to the server.
					state.SetDO (i + 1, false);
				}
			}

		 

			state.UpdateIfModified ();

			resp.ReturnCode = HttpStatusCode.Redirection_SeeOther;
			resp.AddHeader ("Location", "/set");
			resp.SendResponse ();
			// PRG pattern, to avoid problems with post back warnings in the browser: http://en.wikipedia.org/wiki/Post/Redirect/Get
		}

		private static void HttpGetXml (HttpServerResponse resp, HttpServerRequest req)
		{
			HttpGetSensorData (resp, req, "text/xml", new SensorDataXmlExport (resp.TextWriter));
		}

		private static void HttpGetJson (HttpServerResponse resp, HttpServerRequest req)
		{
			HttpGetSensorData (resp, req, "application/json", new SensorDataJsonExport (resp.TextWriter));
		}

		private static void HttpGetTurtle (HttpServerResponse resp, HttpServerRequest req)
		{
			HttpGetSensorData (resp, req, "text/turtle", new SensorDataTurtleExport (resp.TextWriter, req));
		}

		private static void HttpGetRdf (HttpServerResponse resp, HttpServerRequest req)
		{
			HttpGetSensorData (resp, req, "application/rdf+xml", new SensorDataRdfExport (resp.TextWriter, req));
		}

		private static void HttpGetSensorData (HttpServerResponse resp, HttpServerRequest req, string ContentType, ISensorDataExport ExportModule)
		{
			ReadoutRequest Request = new ReadoutRequest (req);
			HttpGetSensorData (resp, ContentType, ExportModule, Request);
		}

		private static void HttpGetSensorData (HttpServerResponse resp, string ContentType, ISensorDataExport Output, ReadoutRequest Request)
		{
			DateTime Now = DateTime.Now;
			string s;
			int i;

			resp.ContentType = ContentType;
			resp.Encoding = System.Text.Encoding.UTF8;
			resp.Expires = DateTime.Now;
			resp.ReturnCode = HttpStatusCode.Successful_OK;

			Output.Start ();
			Output.StartNode ("Actuator");
			Output.StartTimestamp (Now);

			if ((Request.Types & ReadoutType.MomentaryValues) != 0 && Request.ReportTimestamp (Now))
			{
				if (Request.ReportField ("Digital Output Count"))
					Output.ExportField ("Digital Output Count", 8, ReadoutType.StatusValues);

				for (i = 0; i < 8; i++)
				{
					s = "Digital Output " + (i + 1).ToString ();
					if (Request.ReportField (s))
						Output.ExportField (s, digitalOutputs [i]);
				}

				if (Request.ReportField ("State"))
					Output.ExportField ("State", alarmThread != null);
			}

			Output.EndTimestamp ();
			Output.EndNode ();
			Output.End ();
		}

		private class WebServiceAPI : HttpServerWebService
		{
			public WebServiceAPI ()
				: base ("/ws")
			{
			}

			public override string Namespace
			{
				get
				{
					return "http://clayster.com/learniot/actuator/ws/1.0/";
				}
			}

			public override bool CanShowTestFormOnRemoteComputers
			{
				get
				{
					return true;	// Since we have authentication on the resource enabled.
				}
			}

			[WebMethod]
			[WebMethodDocumentation ("Returns the current status of the digital output.")]
			public bool GetDigitalOutput (
				[WebMethodParameterDocumentation ("Digital Output Number. Possible values are 1 to 8.")]
				int Nr)
			{
				if (Nr >= 1 && Nr <= 8)
					return digitalOutputs [Nr - 1];
				else
					return false;
			}

			[WebMethod]
			[WebMethodDocumentation ("Sets the value of a specific digital output.")]
			public void SetDigitalOutput (
				[WebMethodParameterDocumentation ("Digital Output Number. Possible values are 1 to 8.")]
				int Nr,
			
				[WebMethodParameterDocumentation ("Output State to set.")]
				bool Value)
			{
				if (Nr >= 1 && Nr <= 8)
				{
					digitalOutputs [Nr - 1] = Value;
					state.SetDO (Nr, Value);
					state.UpdateIfModified ();
				}
			}

			[WebMethod]
			[WebMethodDocumentation ("Returns the current status of all eight digital outputs. Bit 0 corresponds to DO1, and Bit 7 corresponds to DO8.")]
			public byte GetDigitalOutputs ()
			{
				int i;
				byte b = 0;

				for (i = 7; i >= 0; i--)
				{
					b <<= 1;
					if (digitalOutputs [i])
						b |= 1;
				}

				return b;
			}

			[WebMethod]
			[WebMethodDocumentation ("Sets the value of all eight digital outputs.")]
			public void SetDigitalOutputs (
				[WebMethodParameterDocumentation ("Output States to set. Bit 0 corresponds to DO1, and Bit 7 corresponds to DO8.")]
				byte Values)
			{
				int i;
				bool b;

				for (i = 0; i < 8; i++)
				{
					b = (Values & 1) != 0;
					digitalOutputs [i] = b;
					state.SetDO (i + 1, b);
					Values >>= 1;
				}

				state.UpdateIfModified ();
			}

	
		}


	}//end class
}// end namespace

