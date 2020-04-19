﻿namespace Menees.VsTools
{
	#region Using Directives

	using System;
	using System.ComponentModel.Design;
	using System.Diagnostics;
	using System.Globalization;
	using System.Runtime.InteropServices;
	using System.Threading;
	using System.Threading.Tasks;
	using Menees.VsTools.Editor;
	using Menees.VsTools.Tasks;
	using Microsoft;
	using Microsoft.VisualStudio;
	using Microsoft.VisualStudio.OLE.Interop;
	using Microsoft.VisualStudio.Shell;
	using Microsoft.VisualStudio.Shell.Interop;
	using Microsoft.Win32;

	#endregion

	public sealed partial class MainPackage : AsyncPackage
	{
		#region Private Data Members

		private static Options options;

		private CommandProcessor processor;
		private ClassificationFormatManager formatManager;

		#endregion

		#region Constructors

		/// <summary>
		/// Default constructor of the package.
		/// Inside this method you can place any initialization code that does not require
		/// any Visual Studio service because at this point the package object is created but
		/// not sited yet inside Visual Studio environment. The place to do all the other
		/// initialization is the Initialize method.
		/// </summary>
		public MainPackage()
		{
			LogMessage(string.Format("Entering {0} constructor", this.ToString()));
		}

		#endregion

		#region Internal Properties

		internal static Options Options
		{
			get
			{
				ThreadHelper.ThrowIfNotOnUIThread();

				// Ryan Molden from Microsoft says that GetDialogPage caches the result,
				// so we're going to cache it too and make it into a strongly-typed property.
				// I've also verified GetDialogPage's caching implementation in VS11 by looking
				// at it with Reflector.
				// http://social.msdn.microsoft.com/Forums/eu/vsx/thread/303fce01-dfc0-43b3-a578-8b3258c0b83f
				if (options == null)
				{
					ForceLoad();
				}

				return options;
			}
		}

		internal System.IServiceProvider ServiceProvider => this;

		#endregion

		#region Internal Methods

		[Conditional("TRACE")]
		internal static void LogException(Exception ex)
		{
			LogMessage(null, ex);
		}

		[Conditional("TRACE")]
		internal static void LogMessage(string message, Exception ex = null)
		{
			if (ex != null)
			{
				string activityLogMessage = string.IsNullOrEmpty(message) ? ex.ToString() : message + "\r\n" + ex;
				ActivityLog.LogError(typeof(MainPackage).FullName, activityLogMessage);
				Log.Error(typeof(MainPackage), message, ex);
			}
			else
			{
				ActivityLog.LogInformation(typeof(MainPackage).FullName, message);
				Log.Info(typeof(MainPackage), message, ex);
			}
		}

		internal void ShowMessageBox(string message, bool isError = false)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			IVsUIShell uiShell = (IVsUIShell)this.GetService(typeof(SVsUIShell));
			Assumes.Present(uiShell);
			Guid clsid = Guid.Empty;

			// This has a pszTitle parameter, but it isn't used for the MessageBox's title.
			// VS just appends the pszTitle to the front of the pszText and separates it
			// with a couple of newlines.
			ErrorHandler.ThrowOnFailure(uiShell.ShowMessageBox(
				0,
				ref clsid,
				string.Empty,
				message,
				string.Empty,
				0,
				OLEMSGBUTTON.OLEMSGBUTTON_OK,
				OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
				isError ? OLEMSGICON.OLEMSGICON_CRITICAL : OLEMSGICON.OLEMSGICON_INFO,
				0,        // false
				out _));
		}

		#endregion

		#region Protected Methods

		/// <summary>
		/// Initialization of the package; this method is called right after the package is sited, so this is the place
		/// where you can put all the initialization code that rely on services provided by VisualStudio.
		/// </summary>
		protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			LogMessage(string.Format("Entering {0}.Initialize()", this.ToString()));
			try
			{
				await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(true);

				LogMessage(string.Format("After {0}'s base.Initialize()", this.ToString()));

				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

				// From http://msdn.microsoft.com/en-us/library/bb165039.aspx
				options = this.GetDialogPage(typeof(Options)) as Options;

				this.processor = new CommandProcessor(this);

				// Add our command handlers.  Commands must exist in the .vsct file.
#pragma warning disable VSSDK006 // Check whether the result of GetService is null
				if (await this.GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) is OleMenuCommandService mcs)
#pragma warning restore VSSDK006 // Check whether the result of GetService is null
				{
					foreach (Command id in Enum.GetValues(typeof(Command)))
					{
						CommandID commandId = new CommandID(Guids.MeneesVsToolsCommandSet, (int)id);

						// OleMenuCommand extends the base MenuCommand to add BeforeQueryStatus.
						// http://msdn.microsoft.com/en-us/library/bb165468.aspx
						OleMenuCommand menuItem = new OleMenuCommand(this.Command_Execute, commandId);
						menuItem.BeforeQueryStatus += this.Command_QueryStatus;
						mcs.AddCommand(menuItem);
					}
				}

				// To make our font and color formats customizable in non-TextEditor windows,
				// (e.g., Output) we have to hook some old-school TextManager COM events.
				this.formatManager = new ClassificationFormatManager(this.ServiceProvider);
				this.formatManager.UpdateFormats();
			}
			catch (Exception ex)
			{
				LogMessage(string.Format("An unhandled exception occurred in {0}.Initialize().", this.ToString()), ex);
				throw;
			}

			LogMessage(string.Format("Exiting {0}.Initialize()", this.ToString()));
		}

		#endregion

		#region Private Methods

		// From https://github.com/madskristensen/TrailingWhitespace/blob/master/src/VSPackage.cs
		// See also: https://docs.microsoft.com/en-us/visualstudio/extensibility/loading-vspackages?view=vs-2019#force-a-vspackage-to-load
		private static void ForceLoad()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			var shell = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
			Assumes.Present(shell);

			Guid loadPackage = Guids.MeneesVsToolsPackage;
			shell.LoadPackage(ref loadPackage, out _);
		}

		#endregion

		#region Private Event Handlers

		private void Command_Execute(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (sender is OleMenuCommand menuCommand)
			{
				Command command = (Command)menuCommand.CommandID.ID;
				this.processor.Execute(command);
			}
		}

		private void Command_QueryStatus(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (sender is OleMenuCommand menuCommand)
			{
				Command command = (Command)menuCommand.CommandID.ID;
				bool canExecute = this.processor.CanExecute(command);
				menuCommand.Enabled = canExecute;
				menuCommand.Visible = canExecute;
			}
		}

		#endregion
	}
}
