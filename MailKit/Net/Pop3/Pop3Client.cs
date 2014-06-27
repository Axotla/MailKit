//
// Pop3Client.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2013-2014 Xamarin Inc. (www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Collections.Generic;

#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Encoding = Portable.Text.Encoding;
using MD5 = MimeKit.Cryptography.MD5;
#else
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SslProtocols = System.Security.Authentication.SslProtocols;
using MD5 = System.Security.Cryptography.MD5CryptoServiceProvider;
#endif

using MimeKit;

using MailKit.Security;

namespace MailKit.Net.Pop3 {
	/// <summary>
	/// A POP3 client that can be used to retrieve messages from a server.
	/// </summary>
	/// <remarks>
	/// The <see cref="Pop3Client"/> class supports both the "pop3" and "pop3s"
	/// protocols. The "pop3" protocol makes a clear-text connection to the POP3
	/// server and does not use SSL or TLS unless the POP3 server supports the
	/// STLS extension (as defined by rfc2595). The "pop3s" protocol,
	/// however, connects to the POP3 server using an SSL-wrapped connection.
	/// </remarks>
	public class Pop3Client : MailSpool
	{
		[Flags]
		enum ProbedCapabilities : byte {
			None   = 0,
			Top    = (1 << 0),
			UIDL   = (1 << 1),
			User   = (1 << 2),
		}

		readonly Dictionary<string, int> uids = new Dictionary<string, int> ();
		readonly IProtocolLogger logger;
		readonly Pop3Engine engine;
		ProbedCapabilities probed;
#if NETFX_CORE
		StreamSocket socket;
#endif
		int timeout = 100000;
		bool disposed;
		string host;
		int count;

		/// <summary>
		/// Initializes a new instance of the <see cref="MailKit.Net.Pop3.Pop3Client"/> class.
		/// </summary>
		/// <remarks>
		/// Before you can retrieve messages with the <see cref="Pop3Client"/>, you must first
		/// call the <see cref="Connect(Uri,CancellationToken)"/> method and authenticate with
		/// the <see cref="Authenticate(ICredentials,CancellationToken)"/> method.
		/// </remarks>
		/// <param name="protocolLogger">The protocol logger.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="protocolLogger"/> is <c>null</c>.
		/// </exception>
		public Pop3Client (IProtocolLogger protocolLogger)
		{
			if (protocolLogger == null)
				throw new ArgumentNullException ("protocolLogger");

			// FIXME: should this take a ParserOptions argument?
			engine = new Pop3Engine ();
			logger = protocolLogger;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="MailKit.Net.Pop3.Pop3Client"/> class.
		/// </summary>
		/// <remarks>
		/// Before you can retrieve messages with the <see cref="Pop3Client"/>, you must first
		/// call the <see cref="Connect(Uri,CancellationToken)"/> method and authenticate with
		/// the <see cref="Authenticate(ICredentials,CancellationToken)"/> method.
		/// </remarks>
		public Pop3Client () : this (new NullProtocolLogger ())
		{
		}

		/// <summary>
		/// Gets the lock object used by the default Async methods.
		/// </summary>
		/// <remarks>
		/// Gets the lock object used by the default Async methods.
		/// </remarks>
		/// <value>The lock object.</value>
		public override object SyncRoot {
			get { return engine; }
		}

		/// <summary>
		/// Gets the protocol supported by the message service.
		/// </summary>
		/// <remarks>
		/// Gets the protocol supported by the message service.
		/// </remarks>
		/// <value>The protocol.</value>
		protected override string Protocol {
			get { return "pop"; }
		}

		/// <summary>
		/// Gets the capabilities supported by the POP3 server.
		/// </summary>
		/// <remarks>
		/// The capabilities will not be known until a successful connection has been made via
		/// the <see cref="Connect(Uri,CancellationToken)"/> method and may change as a side-effect
		/// of the <see cref="Authenticate(ICredentials,CancellationToken)"/> method.
		/// </remarks>
		/// <value>The capabilities.</value>
		/// <exception cref="System.ArgumentException">
		/// Capabilities cannot be enabled, they may only be disabled.
		/// </exception>
		public Pop3Capabilities Capabilities {
			get { return engine.Capabilities; }
			set {
				if ((engine.Capabilities | value) > engine.Capabilities)
					throw new ArgumentException ("Capabilities cannot be enabled, they may only be disabled.", "value");

				engine.Capabilities = value;
			}
		}

		/// <summary>
		/// Gets the expiration policy.
		/// </summary>
		/// <remarks>
		/// <para>If the server supports the EXPIRE capability (<see cref="Pop3Capabilities.Expire"/>), the value
		/// of the <see cref="ExpirePolicy"/> property will reflect the value advertized by the server.</para>
		/// <para>A value of <c>-1</c> indicates that messages will never expire.</para>
		/// <para>A value of <c>0</c> indicates that messages that have been retrieved during the current session
		/// will be purged immediately after the connection is closed via the "QUIT" command.</para>
		/// <para>Values larger than <c>0</c> indicate the minimum number of days that the server will retain
		/// messages which have been retrieved.</para>
		/// </remarks>
		/// <value>The expiration policy.</value>
		public int ExpirePolicy {
			get { return engine.ExpirePolicy; }
		}

		/// <summary>
		/// Gets the implementation details of the server.
		/// </summary>
		/// <remarks>
		/// If the server advertizes its implementation details, this value will be set to a string containing the
		/// information details provided by the server.
		/// </remarks>
		/// <value>The implementation details.</value>
		public string Implementation {
			get { return engine.Implementation; }
		}

		/// <summary>
		/// Gets the minimum delay, in milliseconds, between logins.
		/// </summary>
		/// <remarks>
		/// If the server supports the LOGIN-DELAY capability (<see cref="Pop3Capabilities.LoginDelay"/>), this value
		/// will be set to the minimum number of milliseconds that the client must wait between logins.
		/// </remarks>
		/// <value>The login delay.</value>
		public int LoginDelay {
			get { return engine.LoginDelay; }
		}

		void CheckDisposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("Pop3Client");
		}

		void CheckConnected ()
		{
			if (!IsConnected)
				throw new InvalidOperationException ("The Pop3Client is not connected.");
		}

#if !NETFX_CORE
		static bool ValidateRemoteCertificate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
		{
			if (ServicePointManager.ServerCertificateValidationCallback != null)
				return ServicePointManager.ServerCertificateValidationCallback (sender, certificate, chain, errors);

			return true;
		}
#endif

		static Exception CreatePop3Exception (Pop3Command pc)
		{
			var command = pc.Command.Split (' ')[0].TrimEnd ();
			var message = string.Format ("POP3 server did not respond with a +OK response to the {0} command.", command);

			if (pc.Status == Pop3CommandStatus.Error)
				return new Pop3CommandException (message);

			return new Pop3ProtocolException (message);
		}

		static ProtocolException CreatePop3ParseException (string format, params object[] args)
		{
			return new Pop3ProtocolException (string.Format (format, args));
		}

		void SendCommand (CancellationToken token, string command)
		{
			var pc = engine.QueueCommand (token, null, command);

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);
		}

		void SendCommand (CancellationToken token, string format, params object[] args)
		{
			var pc = engine.QueueCommand (token, null, format, args);

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);
		}

		#region IMailService implementation

		/// <summary>
		/// Gets the authentication mechanisms supported by the POP3 server.
		/// </summary>
		/// <remarks>
		/// <para>The authentication mechanisms are queried durring the <see cref="Connect(Uri,CancellationToken)"/> method.</para>
		/// <para>Servers that do not support the SASL capability will typically support either the
		/// <c>"APOP"</c> authentication mechanism (<see cref="Pop3Capabilities.Apop"/>) or the ability to
		/// login using the <c>"USER"</c> and <c>"PASS"</c> commands (<see cref="Pop3Capabilities.User"/>).</para>
		/// </remarks>
		/// <value>The authentication mechanisms.</value>
		public override HashSet<string> AuthenticationMechanisms {
			get { return engine.AuthenticationMechanisms; }
		}

		/// <summary>
		/// Gets or sets the timeout for network streaming operations, in milliseconds.
		/// </summary>
		/// <remarks>
		/// Gets or sets the underlying socket stream's <see cref="System.IO.Stream.ReadTimeout"/>
		/// and <see cref="System.IO.Stream.WriteTimeout"/> values.
		/// </remarks>
		/// <value>The timeout in milliseconds.</value>
		public override int Timeout {
			get { return timeout; }
			set {
				if (IsConnected && engine.Stream.CanTimeout) {
					engine.Stream.WriteTimeout = value;
					engine.Stream.ReadTimeout = value;
				}

				timeout = value;
			}
		}

		/// <summary>
		/// Gets whether or not the client is currently connected to an POP3 server.
		/// </summary>
		/// <remarks>
		/// When a <see cref="Pop3ProtocolException"/> is caught, the connection state of the
		/// <see cref="Pop3Client"/> should be checked before continuing.
		/// </remarks>
		/// <value><c>true</c> if the client is connected; otherwise, <c>false</c>.</value>
		public override bool IsConnected {
			get { return engine.IsConnected; }
		}

		void ProbeCapabilities (CancellationToken cancellationToken)
		{
			if ((engine.Capabilities & Pop3Capabilities.UIDL) == 0) {
				// first, get the message count...
				GetMessageCount (cancellationToken);

				// if the message count is > 0, we can probe the UIDL command
				if (count > 0) {
					try {
						GetMessageUid (0, cancellationToken);
					} catch (NotSupportedException) {
					}
				}
			}
		}

		/// <summary>
		/// Authenticates using the supplied credentials.
		/// </summary>
		/// <remarks>
		/// <para>If the POP3 server supports the APOP authentication mechanism,
		/// then APOP is used.</para>
		/// <para>If the APOP authentication mechanism is not supported and the
		/// server supports one or more SASL authentication mechanisms, then
		/// the SASL mechanisms that both the client and server support are tried
		/// in order of greatest security to weakest security. Once a SASL
		/// authentication mechanism is found that both client and server support,
		/// the credentials are used to authenticate.</para>
		/// <para>If the server does not support SASL or if no common SASL mechanisms
		/// can be found, then the USER and PASS commands are used as a fallback.</para>
		/// </remarks>
		/// <param name="credentials">The user's credentials.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="credentials"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected or is already authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="MailKit.Security.AuthenticationException">
		/// Authentication using the supplied credentials has failed.
		/// </exception>
		/// <exception cref="MailKit.Security.SaslException">
		/// A SASL authentication error occurred.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// A POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// An POP3 protocol error occurred.
		/// </exception>
		public override void Authenticate (ICredentials credentials, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (credentials == null)
				throw new ArgumentNullException ("credentials");

			if (!IsConnected)
				throw new InvalidOperationException ("The Pop3Client must be connected before you can authenticate.");

			if (engine.State == Pop3EngineState.Transaction)
				throw new InvalidOperationException ("The Pop3Client is already authenticated.");

			CheckDisposed ();

			var uri = new Uri ("pop://" + host);
			NetworkCredential cred;
			string challenge;
			Pop3Command pc;

			if ((engine.Capabilities & Pop3Capabilities.Apop) != 0) {
				cred = credentials.GetCredential (uri, "APOP");
				challenge = engine.ApopToken + cred.Password;
				var md5sum = new StringBuilder ();
				byte[] digest;

				using (var md5 = new MD5 ()) {
					digest = md5.ComputeHash (Encoding.UTF8.GetBytes (challenge));
				}

				for (int i = 0; i < digest.Length; i++)
					md5sum.Append (digest[i].ToString ("x2"));

				try {
					SendCommand (cancellationToken, "APOP {0} {1}", cred.UserName, md5sum);
					engine.State = Pop3EngineState.Transaction;
				} catch (Pop3CommandException) {
				}

				if (engine.State == Pop3EngineState.Transaction) {
					engine.QueryCapabilities (cancellationToken);
					ProbeCapabilities (cancellationToken);
					OnAuthenticated ();
					return;
				}
			}

			if ((engine.Capabilities & Pop3Capabilities.Sasl) != 0) {
				foreach (var authmech in SaslMechanism.AuthMechanismRank) {
					if (!engine.AuthenticationMechanisms.Contains (authmech))
						continue;

					var sasl = SaslMechanism.Create (authmech, uri, credentials);

					cancellationToken.ThrowIfCancellationRequested ();

					pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
						while (!sasl.IsAuthenticated && cmd.Status == Pop3CommandStatus.Continue) {
							challenge = sasl.Challenge (text);

							var buf = Encoding.ASCII.GetBytes (challenge + "\r\n");
							pop3.Stream.Write (buf, 0, buf.Length, cmd.CancellationToken);

							var response = pop3.ReadLine (cmd.CancellationToken);

							cmd.Status = Pop3Engine.GetCommandStatus (response, out text);
							if (cmd.Status == Pop3CommandStatus.ProtocolError)
								throw new Pop3ProtocolException (string.Format ("Unexpected response from server: {0}", response));
						}
					}, "AUTH {0}", authmech);

					while (engine.Iterate () < pc.Id) {
						// continue processing commands
					}

					if (pc.Status == Pop3CommandStatus.Error)
						continue;

					if (pc.Status != Pop3CommandStatus.Ok)
						throw CreatePop3Exception (pc);

					if (pc.Exception != null)
						throw pc.Exception;

					engine.State = Pop3EngineState.Transaction;
					engine.QueryCapabilities (cancellationToken);
					ProbeCapabilities (cancellationToken);
					OnAuthenticated ();
					return;
				}
			}

			// fall back to the classic USER & PASS commands...
			cred = credentials.GetCredential (uri, "DEFAULT");

			try {
				SendCommand (cancellationToken, "USER {0}", cred.UserName);
				SendCommand (cancellationToken, "PASS {0}", cred.Password);
			} catch (Pop3CommandException) {
				throw new AuthenticationException ();
			}

			engine.State = Pop3EngineState.Transaction;
			engine.QueryCapabilities (cancellationToken);
			ProbeCapabilities (cancellationToken);
			OnAuthenticated ();
		}

		internal void ReplayConnect (string hostName, Stream replayStream, CancellationToken cancellationToken)
		{
			CheckDisposed ();

			if (hostName == null)
				throw new ArgumentNullException ("hostName");

			if (replayStream == null)
				throw new ArgumentNullException ("replayStream");

			probed = ProbedCapabilities.None;
			host = hostName;

			engine.Connect (new Pop3Stream (replayStream, null, logger), cancellationToken);
			engine.QueryCapabilities (cancellationToken);
			engine.Disconnected += OnEngineDisconnected;
			OnConnected ();
		}

		/// <summary>
		/// Establishes a connection to the specified POP3 server.
		/// </summary>
		/// <remarks>
		/// <para>Establishes a connection to an POP3 or POP3/S server. If the schema
		/// in the uri is "pop", a clear-text connection is made and defaults to using
		/// port 110 if no port is specified in the URI. However, if the schema in the
		/// uri is "pops", an SSL connection is made using the
		/// <see cref="MailService.ClientCertificates"/> and defaults to port 995 unless a port
		/// is specified in the URI.</para>
		/// <para>It should be noted that when using a clear-text POP3 connection,
		/// if the server advertizes support for the STLS extension, the client
		/// will automatically switch into TLS mode before authenticating unless
		/// the <paramref name="uri"/> contains a query string to disable it.</para>
		/// If a successful connection is made, the <see cref="AuthenticationMechanisms"/>
		/// and <see cref="Capabilities"/> properties will be populated.
		/// </remarks>
		/// <param name="uri">The server URI. The <see cref="System.Uri.Scheme"/> should either
		/// be "pop" to make a clear-text connection or "pops" to make an SSL connection.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// The <paramref name="uri"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// The <paramref name="uri"/> is not an absolute URI.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="Pop3Client"/> is already connected.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// A POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override void Connect (Uri uri, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (uri == null)
				throw new ArgumentNullException ("uri");

			if (!uri.IsAbsoluteUri)
				throw new ArgumentException ("The uri must be absolute.", "uri");

			CheckDisposed ();

			if (IsConnected)
				throw new InvalidOperationException ("The Pop3Client is already connected.");

			var scheme = uri.Scheme.ToLowerInvariant ();
			var pops = scheme == "pops" || scheme == "pop3s";
			var port = uri.Port > 0 ? uri.Port : (pops ? 995 : 110);
			var query = uri.ParsedQuery ();
			Stream stream;
			string value;

			var starttls = !pops && (!query.TryGetValue ("starttls", out value) || Convert.ToBoolean (value));

#if !NETFX_CORE
			var ipAddresses = Dns.GetHostAddresses (uri.DnsSafeHost);
			Socket socket = null;

			for (int i = 0; i < ipAddresses.Length; i++) {
				socket = new Socket (ipAddresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				try {
					cancellationToken.ThrowIfCancellationRequested ();
					socket.Connect (ipAddresses[i], port);
					break;
				} catch (OperationCanceledException) {
					socket.Dispose ();
					throw;
				} catch {
					socket.Dispose ();

					if (i + 1 == ipAddresses.Length)
						throw;
				}
			}

			if (pops) {
				var ssl = new SslStream (new NetworkStream (socket, true), false, ValidateRemoteCertificate);
				ssl.AuthenticateAsClient (uri.Host, ClientCertificates, SslProtocols.Default, true);
				stream = ssl;
			} else {
				stream = new NetworkStream (socket, true);
			}
#else
			socket = new StreamSocket ();

			try {
				cancellationToken.ThrowIfCancellationRequested ();
				socket.ConnectAsync (new HostName (uri.DnsSafeHost), port.ToString (), pops ? SocketProtectionLevel.Tls12 : SocketProtectionLevel.PlainSocket)
					.AsTask (cancellationToken)
					.GetAwaiter ()
					.GetResult ();
			} catch {
				socket.Dispose ();
				socket = null;
				throw;
			}

			stream = new DuplexStream (socket.InputStream.AsStreamForRead (0), socket.OutputStream.AsStreamForWrite (0));
#endif

			probed = ProbedCapabilities.None;
			if (stream.CanTimeout) {
				stream.WriteTimeout = timeout;
				stream.ReadTimeout = timeout;
			}

			host = uri.Host;

			logger.LogConnect (uri);

			engine.Connect (new Pop3Stream (stream, socket, logger), cancellationToken);
			engine.QueryCapabilities (cancellationToken);

			if (starttls && (engine.Capabilities & Pop3Capabilities.StartTLS) != 0) {
				SendCommand (cancellationToken, "STLS");

#if !NETFX_CORE
				var tls = new SslStream (stream, false, ValidateRemoteCertificate);
				tls.AuthenticateAsClient (uri.Host, ClientCertificates, SslProtocols.Tls, true);
				engine.Stream.Stream = tls;
#else
				socket.UpgradeToSslAsync (SocketProtectionLevel.Tls12, new HostName (uri.DnsSafeHost))
					.AsTask (cancellationToken)
					.GetAwaiter ()
					.GetResult ();
#endif

				// re-issue a CAPA command
				engine.QueryCapabilities (cancellationToken);
			}

			engine.Disconnected += OnEngineDisconnected;
			OnConnected ();
		}

		/// <summary>
		/// Disconnect the service.
		/// </summary>
		/// <remarks>
		/// If <paramref name="quit"/> is <c>true</c>, a "QUIT" command will be issued in order to disconnect cleanly.
		/// </remarks>
		/// <param name="quit">If set to <c>true</c>, a "QUIT" command will be issued in order to disconnect cleanly.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		public override void Disconnect (bool quit, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();

			if (!engine.IsConnected)
				return;

			if (quit) {
				try {
					SendCommand (cancellationToken, "QUIT");
				} catch (OperationCanceledException) {
				} catch (Pop3ProtocolException) {
				} catch (Pop3CommandException) {
				} catch (IOException) {
				}
			}

			#if NETFX_CORE
			socket.Dispose ();
			socket = null;
			#endif

			uids.Clear ();
			count = 0;

			engine.Disconnect ();
		}

		/// <summary>
		/// Ping the POP3 server to keep the connection alive.
		/// </summary>
		/// <remarks>Mail servers, if left idle for too long, will automatically drop the connection.</remarks>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected or authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override void NoOp (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new InvalidOperationException ("You must be authenticated before you can issue a NOOP command.");

			SendCommand (cancellationToken, "NOOP");
		}

		void OnEngineDisconnected (object sender, EventArgs e)
		{
			engine.Disconnected -= OnEngineDisconnected;
			OnDisconnected ();
		}

		#endregion

		#region IMailSpool implementation

		/// <summary>
		/// Gets whether or not the <see cref="Pop3Client"/> supports referencing messages by UIDs.
		/// </summary>
		/// <remarks>
		/// <para>Not all servers support referencing messages by UID, so this property should
		/// be checked before using <see cref="GetMessageUid(int, CancellationToken)"/>
		/// and <see cref="GetMessageUids(CancellationToken)"/>.</para>
		/// <para>If the server does not support UIDs, then all methods that take UID arguments
		/// along with <see cref="GetMessageUid(int, CancellationToken)"/> and
		/// <see cref="GetMessageUids(CancellationToken)"/> will fail.</para>
		/// </remarks>
		/// <value><c>true</c> if supports UIDs; otherwise, <c>false</c>.</value>
		public override bool SupportsUids {
			get { return (engine.Capabilities & Pop3Capabilities.UIDL) != 0; }
		}

		/// <summary>
		/// Get the number of messages available in the message spool.
		/// </summary>
		/// <remarks>
		/// Gets the number of messages available in the message spool.
		/// </remarks>
		/// <returns>The number of available messages.</returns>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override int GetMessageCount (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			var pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				// the response should be "<count> <total size>"
				var tokens = text.Split (new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

				if (tokens.Length < 2) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an incomplete response to the STAT command.");
					return;
				}

				if (!int.TryParse (tokens[0], out count)) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an invalid response to the STAT command.");
					return;
				}
			}, "STAT");

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);

			if (pc.Exception != null)
				throw pc.Exception;

			return count;
		}

		/// <summary>
		/// Get the UID of the message at the specified index.
		/// </summary>
		/// <remarks>
		/// Not all servers support UIDs, so you should first check
		/// the <see cref="SupportsUids"/> property.
		/// </remarks>
		/// <returns>The message UID.</returns>
		/// <param name="index">The message index.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is not a valid message index.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The POP3 server does not support the UIDL extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override string GetMessageUid (int index, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (!SupportsUids && (probed & ProbedCapabilities.UIDL) != 0)
				throw new NotSupportedException ("The POP3 server does not support the UIDL extension.");

			if (index < 0 || index >= count)
				throw new ArgumentOutOfRangeException ("index");

			string uid = null;

			var pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				// the response should be "<seqid> <uid>"
				var tokens = text.Split (new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				int seqid;

				if (tokens.Length < 2) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an incomplete response to the UIDL command.");
					return;
				}

				if (!int.TryParse (tokens[0], out seqid) || seqid < 1) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an unexpected response to the UIDL command.");
					return;
				}

				if (seqid != index + 1) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned the UID for the wrong message.");
					return;
				}

				uid = tokens[1];
			}, "UIDL {0}", index + 1);

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			probed |= ProbedCapabilities.UIDL;

			if (pc.Status != Pop3CommandStatus.Ok) {
				if (!SupportsUids)
					throw new NotSupportedException ("The POP3 server does not support the UIDL extension.");

				throw CreatePop3Exception (pc);
			}

			if (pc.Exception != null)
				throw pc.Exception;

			engine.Capabilities |= Pop3Capabilities.UIDL;

			uids[uid] = index + 1;

			return uid;
		}

		/// <summary>
		/// Get the full list of available message UIDs.
		/// </summary>
		/// <remarks>
		/// Not all servers support UIDs, so you should first check
		/// the <see cref="SupportsUids"/> property.
		/// </remarks>
		/// <returns>The message uids.</returns>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The POP3 server does not support the UIDL extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override string[] GetMessageUids (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (!SupportsUids && (probed & ProbedCapabilities.UIDL) != 0)
				throw new NotSupportedException ("The POP3 server does not support the UIDL extension.");

			uids.Clear ();

			var pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				do {
					var response = engine.ReadLine (cmd.CancellationToken).TrimEnd ();
					if (response == ".")
						break;

					if (cmd.Exception != null)
						continue;

					var tokens = response.Split (new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
					int seqid;

					if (tokens.Length < 2) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned an incomplete response to the UIDL command.");
						continue;
					}

					if (!int.TryParse (tokens[0], out seqid)) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned an invalid response to the UIDL command.");
						continue;
					}

					uids.Add (tokens[1], seqid);
				} while (true);
			}, "UIDL");

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			probed |= ProbedCapabilities.UIDL;

			if (pc.Status != Pop3CommandStatus.Ok) {
				if (!SupportsUids)
					throw new NotSupportedException ("The POP3 server does not support the UIDL extension.");

				throw CreatePop3Exception (pc);
			}

			if (pc.Exception != null)
				throw pc.Exception;

			engine.Capabilities |= Pop3Capabilities.UIDL;

			return uids.Keys.ToArray ();
		}

		int GetMessageSizeForSequenceId (int seqid, CancellationToken cancellationToken)
		{
			int size = -1;

			var pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				var tokens = text.Split (new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				int id;

				if (tokens.Length < 2) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an incomplete response to the LIST command.");
					return;
				}

				if (!int.TryParse (tokens[0], out id) || id < 1) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an unexpected response to the LIST command.");
					return;
				}

				if (id != seqid) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned the size for the wrong message.");
					return;
				}

				if (!int.TryParse (tokens[1], out size) || size < 0) {
					cmd.Exception = CreatePop3ParseException ("Pop3 server returned an unexpected size token to the LIST command.");
					return;
				}
			}, "LIST {0}", seqid);

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);

			if (pc.Exception != null)
				throw pc.Exception;

			return size;
		}

		/// <summary>
		/// Get the size of the specified message, in bytes.
		/// </summary>
		/// <remarks>
		/// Gets the size of the specified message, in bytes.
		/// </remarks>
		/// <returns>The message size, in bytes.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="uid"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is not a valid message UID.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override int GetMessageSize (string uid, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (uid == null)
				throw new ArgumentNullException ("uid");

			int seqid;

			if (!uids.TryGetValue (uid, out seqid))
				throw new ArgumentException ("No such message.", "uid");

			return GetMessageSizeForSequenceId (seqid, cancellationToken);
		}

		/// <summary>
		/// Get the size of the specified message, in bytes.
		/// </summary>
		/// <remarks>
		/// Gets the size of the specified message, in bytes.
		/// </remarks>
		/// <returns>The message size, in bytes.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is not a valid message index.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override int GetMessageSize (int index, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (index < 0 || index >= count)
				throw new ArgumentOutOfRangeException ("index");

			return GetMessageSizeForSequenceId (index + 1, cancellationToken);
		}

		/// <summary>
		/// Get the sizes for all available messages, in bytes.
		/// </summary>
		/// <remarks>
		/// Gets the sizes for all available messages, in bytes.
		/// </remarks>
		/// <returns>The message sizes, in bytes.</returns>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override int[] GetMessageSizes (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			var sizes = new List<int> ();

			var pc = engine.QueueCommand (cancellationToken, (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				do {
					var response = engine.ReadLine (cmd.CancellationToken).TrimEnd ();
					if (response == ".")
						break;

					if (cmd.Exception != null)
						continue;

					var tokens = response.Split (new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
					int seqid, size;

					if (tokens.Length < 2) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned an incomplete response to the LIST command.");
						continue;
					}

					if (!int.TryParse (tokens[0], out seqid) || seqid < 1) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned an unexpected response to the LIST command.");
						continue;
					}

					if (seqid != sizes.Count + 1) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned the size for the wrong message.");
						continue;
					}

					if (!int.TryParse (tokens[1], out size) || size < 0) {
						cmd.Exception = CreatePop3ParseException ("Pop3 server returned an unexpected size token to the LIST command.");
						continue;
					}

					sizes.Add (size);
				} while (true);
			}, "LIST");

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);

			if (pc.Exception != null)
				throw pc.Exception;

			return sizes.ToArray ();
		}

		MimeMessage GetMessageForSequenceId (int seqid, bool headersOnly, CancellationToken cancellationToken)
		{
			MimeMessage message = null;
			Pop3Command pc;

			Pop3CommandHandler handler = (pop3, cmd, text) => {
				if (cmd.Status != Pop3CommandStatus.Ok)
					return;

				try {
					pop3.Stream.Mode = Pop3StreamMode.Data;
					message = MimeMessage.Load (pop3.Stream, cancellationToken);
				} finally {
					pop3.Stream.Mode = Pop3StreamMode.Line;
				}
			};

			if (headersOnly)
				pc = engine.QueueCommand (cancellationToken, handler, "TOP {0} 0", seqid);
			else
				pc = engine.QueueCommand (cancellationToken, handler, "RETR {0}", seqid);

			while (engine.Iterate () < pc.Id) {
				// continue processing commands
			}

			if (pc.Status != Pop3CommandStatus.Ok)
				throw CreatePop3Exception (pc);

			if (pc.Exception != null)
				throw pc.Exception;

			return message;
		}

		/// <summary>
		/// Get the headers for the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the headers for the specified message.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="uid"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is not a valid message UID.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The POP3 server does not support the UIDL extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override HeaderList GetMessageHeaders (string uid, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (uid == null)
				throw new ArgumentNullException ("uid");

			int seqid;

			if (!uids.TryGetValue (uid, out seqid))
				throw new ArgumentException ("No such message.", "uid");

			return GetMessageForSequenceId (seqid, true, cancellationToken).Headers;
		}

		/// <summary>
		/// Get the headers for the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the headers for the specified message.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is not a valid message index.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override HeaderList GetMessageHeaders (int index, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (index < 0 || index >= count)
				throw new ArgumentOutOfRangeException ("index");

			return GetMessageForSequenceId (index + 1, true, cancellationToken).Headers;
		}

		/// <summary>
		/// Get the message with the specified UID.
		/// </summary>
		/// <remarks>
		/// Gets the message with the specified UID.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="uid"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is not a valid message UID.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The POP3 server does not support the UIDL extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override MimeMessage GetMessage (string uid, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (uid == null)
				throw new ArgumentNullException ("uid");

			int seqid;

			if (!uids.TryGetValue (uid, out seqid))
				throw new ArgumentException ("No such message.", "uid");

			return GetMessageForSequenceId (seqid, false, cancellationToken);
		}

		/// <summary>
		/// Get the message at the specified index.
		/// </summary>
		/// <remarks>
		/// Gets the message at the specified index.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is not a valid message index.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override MimeMessage GetMessage (int index, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (index < 0 || index >= count)
				throw new ArgumentOutOfRangeException ("index");

			return GetMessageForSequenceId (index + 1, false, cancellationToken);
		}

		/// <summary>
		/// Mark the specified message for deletion.
		/// </summary>
		/// <remarks>
		/// Messages marked for deletion are not actually deleted until the session
		/// is cleanly disconnected
		/// (see <see cref="IMailService.Disconnect(bool, CancellationToken)"/>).
		/// </remarks>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="uid"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is not a valid message UID.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The POP3 server does not support the UIDL extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override void DeleteMessage (string uid, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (uid == null)
				throw new ArgumentNullException ("uid");

			int seqid;

			if (!uids.TryGetValue (uid, out seqid))
				throw new ArgumentException ("No such message.", "uid");

			SendCommand (cancellationToken, "DELE {0}", seqid);
		}

		/// <summary>
		/// Mark the specified message for deletion.
		/// </summary>
		/// <remarks>
		/// Messages marked for deletion are not actually deleted until the session
		/// is cleanly disconnected
		/// (see <see cref="IMailService.Disconnect(bool, CancellationToken)"/>).
		/// </remarks>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is not a valid message index.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override void DeleteMessage (int index, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			if (index < 0 || index >= count)
				throw new ArgumentOutOfRangeException ("index");

			SendCommand (cancellationToken, "DELE {0}", index + 1);
		}

		/// <summary>
		/// Reset the state of all messages marked for deletion.
		/// </summary>
		/// <remarks>
		/// Messages marked for deletion are not actually deleted until the session
		/// is cleanly disconnected
		/// (see <see cref="IMailService.Disconnect(bool, CancellationToken)"/>).
		/// </remarks>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// The POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override void Reset (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			SendCommand (cancellationToken, "RSET");
		}

		#endregion

		#region IEnumerable<MimeMessage> implementation

		/// <summary>
		/// Gets an enumerator for the messages in the folder.
		/// </summary>
		/// <remarks>
		/// Gets an enumerator for the messages in the folder.
		/// </remarks>
		/// <returns>The enumerator.</returns>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="Pop3Client"/> has been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// The <see cref="Pop3Client"/> is not connected.
		/// </exception>
		/// <exception cref="System.UnauthorizedAccessException">
		/// The <see cref="Pop3Client"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="Pop3CommandException">
		/// A POP3 command failed.
		/// </exception>
		/// <exception cref="Pop3ProtocolException">
		/// A POP3 protocol error occurred.
		/// </exception>
		public override IEnumerator<MimeMessage> GetEnumerator ()
		{
			CheckDisposed ();
			CheckConnected ();

			if (engine.State != Pop3EngineState.Transaction)
				throw new UnauthorizedAccessException ();

			int n = GetMessageCount (CancellationToken.None);

			for (int i = 0; i < n; i++)
				yield return GetMessage (i, CancellationToken.None);

			yield break;
		}

		#endregion

		/// <summary>
		/// Releases the unmanaged resources used by the <see cref="Pop3Client"/> and
		/// optionally releases the managed resources.
		/// </summary>
		/// <remarks>
		/// Releases the unmanaged resources used by the <see cref="Pop3Client"/> and
		/// optionally releases the managed resources.
		/// </remarks>
		/// <param name="disposing"><c>true</c> to release both managed and unmanaged resources;
		/// <c>false</c> to release only the unmanaged resources.</param>
		protected override  void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				engine.Disconnect ();
				logger.Dispose ();

#if NETFX_CORE
				if (socket != null)
					socket.Dispose ();
#endif

				disposed = true;
			}
		}
	}
}
