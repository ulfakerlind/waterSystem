
using SimpleHttpServer;
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace WaterSystem
{
	public class Server : HttpServer
	{

		public delegate bool Set();
		public delegate bool SetInput(int a);
		Set SetHigh;
		Set SetLow;
		Set SetRefilled;
		SetInput SetNewWaterFreq;
	
		public Server (Set max , Set min, Set setrefilled, SetInput SetNew , int port) : base(port)
		{
			SetHigh = new Set(max);
			SetLow = new Set(min);
			SetRefilled = new Set(setrefilled);
			SetNewWaterFreq = new SetInput(SetNew);
			


		
        
            
		}

		  private string streamReadLine (Stream inputStream)
		{
			int next_char;
			string data = "";
			while (true) {
				next_char = inputStream.ReadByte ();
				if (next_char == '\n') {
					break;
				}
				if (next_char == '\r') {
					continue;
				}
				if (next_char == -1) {
					Thread.Sleep (1);
					continue;
				}
				;
				data += Convert.ToChar (next_char);
			}
			return data;
		}
          
        public override void handleGETRequest (HttpProcessor p)
		{

			const int BUF_SIZE = 4096;
			if (p.http_url.Equals ("/Test.png")) {
				Stream fs = File.Open ("../../Test.png", FileMode.Open);

				p.writeSuccess ("image/png");

				fs.CopyTo (p.outputStream.BaseStream);
				p.outputStream.BaseStream.Flush ();
			}

			Console.WriteLine ("request: {0}", p.http_url);
        
			p.writeSuccess ();
			using (var gs = File.Open ("../../html/index.html", FileMode.Open)) {
				byte[] buf = new byte[BUF_SIZE];  
				try {
					string line = "";
					while ((line = streamReadLine(gs)) != null) {
						p.outputStream.WriteLine (line);
						if (line == "</html>") {
							break;
						}
					}

				} catch (Exception e) {
					Console.WriteLine (e);
				}
			}

		}

		   public override void handlePOSTRequest(HttpProcessor p, StreamReader inputData) {
            Console.WriteLine("POST request: {0}", p.http_url);
			string data = inputData.ReadToEnd();
			if(data == "max=SetMax")
				this.SetHigh();
			else if(data == "min=SetMin")
				SetLow();
            p.writeSuccess();
            p.outputStream.WriteLine("<html><body><h1>test server</h1>");
            p.outputStream.WriteLine("<a href=/test>return</a><p>");
            p.outputStream.WriteLine("postbody: <pre>{0}</pre>", data);
            

        }
	}
}

