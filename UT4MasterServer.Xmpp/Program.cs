using System.Security.Cryptography.X509Certificates;

namespace UT4MasterServer.Xmpp;

internal class Program
{
	private static async Task Main(string[] args)
	{
		//XmlParser.XmlScanner scanner = new();
		//XmlParser.XmlParserPermissive parsers = new(scanner, File.OpenText("../../../example.xml"));
		//while (true)
		//{
		//	await parsers.ReadElementStartAsync(CancellationToken.None);
		//	Console.WriteLine(parsers.Current.ToString());
		//}

		var cts = new CancellationTokenSource();

		var cert = new X509Certificate2("../../../Certs/master-ut4.pfx", "");

		var server = new XmppServer("master-ut4.timiimit.com", cert);
		Task? serverTask = server.StartAsync(cts.Token);
		while (true)
		{
			var command = Console.ReadLine();
			if (command == null)
			{
				continue;
			}

			var commandParts = command.Split(' ');

			if (command == "exit")
			{
				break;
			}

			if (commandParts[0] == "send")
			{
				if (commandParts[1] == "message")
				{
					XmppConnection? connection = await server.FindConnectionAsync(JID.Parse(commandParts[2]));
					if (connection == null)
					{
						Console.WriteLine("Could not find connection for '{JID}'.", commandParts[2]);
						continue;
					}

					await server.SendSystemMessageAsync(connection, commandParts[3]);
				}
				else if (commandParts[1] == "presence")
				{

				}
			}
		}

		cts.Cancel();
		serverTask.Wait();
	}
}
