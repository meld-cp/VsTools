﻿namespace Menees.VsTools.Tasks
{
	#region Using Directives

	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.ComponentModel.Design;
	using System.Diagnostics.CodeAnalysis;
	using System.Drawing.Design;
	using System.Linq;
	using System.Runtime.InteropServices;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.Shell;

	#endregion

	// Note: The MainPackage has a ProvideOptionPage attribute that associates this class with that package.
	[Guid(Guids.TaskOptionsString)]
	[DefaultProperty(nameof(AddTodoPrefix))] // Make this get focus in the PropertyGrid first since its category is alphabetically first.
	[SuppressMessage("Internal class never created.", "CA1812", Justification = "Created via reflection by VS.")]
	internal sealed class Options : OptionsBase
	{
		#region Private Data Members

		private const string DefaultExcludeFilesPatterns = @".+\.Designer\.\w+$" + "\r\n" +
			@"modernizr-\d+\.\d+\.\d+(-vsdoc)?\.js$" + "\r\n" +
			@"jquery-\d+\.\d+\.\d+(-vsdoc)?\.js$" + "\r\n" +
			@".+\.min\.js$" + "\r\n" +
			@"globalize\..+.js$" + "\r\n" +
			@"ace\.js$" + "\r\n" +
			@"cldr\.js$";

		private const string DefaultExcludeProjectsPatterns = @".+\.(sql|vc|vcx)proj$";
		private const string DefaultTodoPrefix = "TODO: ";
		private const TodoSuffix DefaultTodoSuffix = TodoSuffix.UserDate;

		private const int MinParallelism = 1;
		private const int MaxParallelism = 8;
		private const int ProcessorScaleFactor = 4;
		private const string ProcessorScaleFactorPercent = "25%";

		private string excludeFilesPatterns;
		private string excludeProjectsPatterns;
		private string excludeFileComments;
		private string excludeCommentsPatterns;
		private int? requestedMaxDegreeOfParallelism;
		private string todoPrefix;

		#endregion

		#region Constructors

		public Options()
		{
			// Set each public property to force the internal list and set properties to be updated.
			this.ExcludeFilesPatterns = DefaultExcludeFilesPatterns;
			this.ExcludeProjectsPatterns = DefaultExcludeProjectsPatterns;
			this.ExcludeFileComments = null;
			this.ExcludeCommentsPatterns = null;
			this.AddTodoPrefix = DefaultTodoPrefix;
			this.AddTodoSuffix = DefaultTodoSuffix;
		}

		#endregion

		#region Public Browsable Properties (for Options page)

		[Category("Scan")]
		[DisplayName("Enable task scanning (requires restart)")]
		[Description("Whether open documents and files referenced by the current solution should be scanned for task comments.")]
		[DefaultValue(false)] // Off by default since it can have a serious CPU impact on large solutions.
		public bool EnableCommentScans { get; set; }

		[Category("Scan")]
		[DisplayName("Max degree of parallelism")]
		[Description("The maximum number of concurrent file scans to perform. If blank, then "
			+ ProcessorScaleFactorPercent + " of your logical CPU count will be used.")]
		[DefaultValue(null)]
		public int? RequestedMaxDegreeOfParallelism
		{
			get
			{
				return this.requestedMaxDegreeOfParallelism;
			}

			set
			{
				if (value != null && (value.Value < MinParallelism || value.Value > MaxParallelism))
				{
					throw new ArgumentException($"Value must be between {MinParallelism} and {MaxParallelism}.");
				}

				this.requestedMaxDegreeOfParallelism = value;
			}
		}

		[Category("Exclude")]
		[DisplayName("Exclude file name patterns")]
		[Description("Regular expressions used to exclude solution items or open documents from being scanned for comments. " +
			"Enter one pattern per line. Each pattern is matched against the fully-qualified file name.")]
		[Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
		[DefaultValue(DefaultExcludeFilesPatterns)]
		public string ExcludeFilesPatterns
		{
			get
			{
				return this.excludeFilesPatterns;
			}

			set
			{
				string patterns = string.IsNullOrEmpty(value) ? DefaultExcludeFilesPatterns : value;
				this.ExcludeFilesExpressions = SplitPatterns(patterns);
				this.excludeFilesPatterns = patterns;
			}
		}

		[Category("Exclude")]
		[DisplayName("Exclude project name patterns")]
		[Description("Regular expressions used to exclude projects from being recursively scanned for files. " +
			"Enter one pattern per line. Each pattern is matched against the fully-qualified project file path.")]
		[Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
		[DefaultValue(DefaultExcludeProjectsPatterns)]
		public string ExcludeProjectsPatterns
		{
			get
			{
				return this.excludeProjectsPatterns;
			}

			set
			{
				string patterns = string.IsNullOrEmpty(value) ? DefaultExcludeProjectsPatterns : value;
				this.ExcludeProjectsExpressions = SplitPatterns(patterns);
				this.excludeProjectsPatterns = patterns;
			}
		}

		[Category("Exclude")]
		[DisplayName("Exclude file comments")]
		[Description("Exact \"FileName: Comment\" patterns to exclude. Enter one pattern per line. " +
			"This is typically appended to by the Tasks window's right-click Exclude command.")]
		[Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
		[DefaultValue(null)]
		public string ExcludeFileComments
		{
			get
			{
				return this.excludeFileComments;
			}

			set
			{
				this.excludeFileComments = value;
				TextLines lines = new(this.excludeFileComments);
				this.ExcludeFileCommentSet = new HashSet<string>(lines.Lines.Where(line => !string.IsNullOrWhiteSpace(line)), StringComparer.OrdinalIgnoreCase);
			}
		}

		[Category("Exclude")]
		[DisplayName("Exclude comment patterns")]
		[Description("Regular expressions used to exclude task comments. " +
			"Enter one pattern per line. Each pattern is matched against the comments identified by task tokens.")]
		[Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
		[DefaultValue(null)]
		public string ExcludeCommentsPatterns
		{
			get
			{
				return this.excludeCommentsPatterns;
			}

			set
			{
				this.ExcludeCommentsExpressions = SplitPatterns(value);
				this.excludeCommentsPatterns = value;
			}
		}

		[Category("Add")]
		[DisplayName("Comment prefix")]
		[Description("The prefix that the \"Add TODO Comment\" command should add to each comment.")]
		[DefaultValue(DefaultTodoPrefix)]
		public string AddTodoPrefix
		{
			get
			{
				return this.todoPrefix;
			}

			set
			{
				if (string.IsNullOrWhiteSpace(value))
				{
					this.todoPrefix = DefaultTodoPrefix;
				}
				else
				{
					this.todoPrefix = value;
					if (!char.IsWhiteSpace(this.todoPrefix[this.todoPrefix.Length - 1]))
					{
						this.todoPrefix += ' ';
					}
				}
			}
		}

		[Category("Add")]
		[DisplayName("Comment suffix")]
		[Description("The suffix that the \"Add TODO Comment\" command should add to each comment.")]
		[DefaultValue(DefaultTodoSuffix)]
		public TodoSuffix AddTodoSuffix { get; set; }

		#endregion

		#region Public Non-Browsable Properties (for other state persistence)

		[Browsable(false)]
		public string TasksStatusXml { get; set; }

		[Browsable(false)]
		public string FilterTasksBy { get; set; }

		#endregion

		#region Internal Properties

		internal IReadOnlyList<Regex> ExcludeFilesExpressions { get; private set; }

		internal IReadOnlyList<Regex> ExcludeProjectsExpressions { get; private set; }

		internal ISet<string> ExcludeFileCommentSet { get; private set; }

		internal IReadOnlyList<Regex> ExcludeCommentsExpressions { get; private set; }

		internal int MaxDegreeOfParallelism
		{
			get
			{
				int result = this.requestedMaxDegreeOfParallelism
					?? Math.Max(MinParallelism, Math.Min(Environment.ProcessorCount / ProcessorScaleFactor, MaxParallelism));
				return result;
			}
		}

		#endregion

		#region Private Methods

		private static IReadOnlyList<Regex> SplitPatterns(string patterns)
		{
			TextLines lines = new(patterns);

			// If they enter an invalid regular expression, then this will throw an ArgumentException.
			List<Regex> result = lines.Lines.Where(line => !string.IsNullOrEmpty(line))
				.Distinct()
				.Select(line => new Regex(line, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant))
				.ToList();

			return result;
		}

		#endregion
	}
}
