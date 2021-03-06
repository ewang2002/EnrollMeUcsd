﻿#define OVERRIDE_DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandLine;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace EnrollMeUcsd
{
	public static class Program
	{
		private const string ActivePopupCssSelector = "div[class='ui-dialog " +
		                                              "ui-widget ui-widget-content " +
		                                              "ui-corner-all ui-front " +
		                                              "ui-dialog-buttons ui-draggable " +
		                                              "ui-resizable']";

		private static void Wait(IWebDriver driver, Predicate<IWebDriver> func, TimeSpan time)
		{
			var wait = new WebDriverWait(driver, time);
			wait.Until(x => func(x));
		}

		private static IEnumerable<IWebElement> GetPopUpElement(ChromeDriver driver)
			=> driver.FindElementsByCssSelector(ActivePopupCssSelector);

		private static void Log(int type, object content)
		{
			var logType = "INFO";
			if (type == 1)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				logType = "ERROR";
			}

			Console.WriteLine($"{DateTime.Now:[HH:mm:ss]} [{logType}] {content}");
			Console.ResetColor();
		}

		public static async Task Main(string[] args)
		{
			if (args.Length == 0)
				args = Console.ReadLine()?.Split(" ") ?? Array.Empty<string>();

			await Parser.Default.ParseArguments<CliArguments>(args)
				.WithParsedAsync(Run);
		}


		private static async Task Run(CliArguments args)
		{
			Console.Clear();
			args.SectionIds = args.SectionIds
				.Where(x => x != string.Empty)
				.ToArray();
			
			var chromeOptions = new ChromeOptions();
#if !DEBUG || OVERRIDE_DEBUG
			chromeOptions.AddArgument("--headless");
#endif
			chromeOptions.AddArgument("log-level=3");
			var chromeService = ChromeDriverService.CreateDefaultService(AppDomain.CurrentDomain.BaseDirectory);
			chromeService.SuppressInitialDiagnosticInformation = true;
			using var driver = new ChromeDriver(chromeService, chromeOptions);
#if DEBUG && !OVERRIDE_DEBUG
			driver.Manage().Window.Maximize();
#endif

			// Go to WebReg and login. 
			driver.Navigate().GoToUrl("https://act.ucsd.edu/webreg2/start");
			driver.FindElementByName("urn:mace:ucsd.edu:sso:username")
				.SendKeys(args.Username);
			driver.FindElementByName("urn:mace:ucsd.edu:sso:password")
				.SendKeys(args.Password + Keys.Enter);

			// Invalid password.
			if (driver.FindElementsById("_login_error_message").Any())
			{
				Log(1, "Invalid Password. Try Again.");
				driver.Close();
				return;
			}

			try
			{
				Log(0, "Please authenticate this session with Duo 2FA.");
				Wait(driver, x => x.Url.Contains("start"), TimeSpan.FromMinutes(2));
			}
			catch (Exception)
			{
				Log(1, "Failed to authenticate. Try again.");
				driver.Close();
				return;
			}

			Wait(driver, x => x.FindElements(By.Id("startpage-button-go")).Count != 0,
				TimeSpan.FromSeconds(5));
			Log(0, "Logged in successfully.");

			// Click on "Go" 
			driver.FindElementById("startpage-button-go").Click();
			await Task.Delay(TimeSpan.FromSeconds(1));
			// click on "Advanced Search" 
			driver.FindElementById("advanced-search").Click();
			var classesEnrolledIn = new List<string>();
			while (true)
			{
				// Just to be nice to WebReg
				// Also because, occasionally, the buttons may be delayed. 
				await Task.Delay(500);
				foreach (var section in args.SectionIds
					.Where(section => !classesEnrolledIn.Contains(section)))
				{
					// click "Reset" button
					driver.FindElementById("search-div-t-reset").Click();
					// find the "Section ID" search box
					driver.FindElementById("search-div-t-t3-i4").SendKeys(section + Keys.Enter);
					// delay so the table can properly load
					// We know the table is fully loaded when the spinner is gone
					Wait(driver, x => x.FindElements(By.ClassName("wr-spinner-loading")).Count == 0,
						TimeSpan.FromSeconds(5));
					// Account for minor delay. 
					await Task.Delay(50);

					// check to see if there even are rows to check 
					if (driver.FindElementsById("search-group-header-id").Count == 0)
					{
						Log(1, $"Section ID {section} not found. Is the section ID valid?");
						// Check to see if we got a possible system error message 
						// If so, close it and continue. 
						GetPopUpElement(driver)
							.FirstOrDefault(x => x.GetAttribute("style").Contains("display: block;"))?
							.FindElements(By.Id("dialog-msg-close"))
							.FirstOrDefault()?
							.Click();
						continue;
					}

					// click on the first row (there will only be one row)
					driver.FindElementById("search-group-header-id").Click();
					// get the course dept + id
					var courseDeptId = driver.FindElementById("search-group-header-id")
						.FindElement(By.TagName("td"))
						.Text.Trim()
						// because some course dept + id has two spaces
						.Replace("  ", " ");

					// check if the enroll button exists. If it does, click. 
					var enrollButton = driver.FindElementsById($"search-enroll-id-{section}");
					if (enrollButton.Count == 0)
					{
						Log(1, $"Unable to enroll in {courseDeptId} ({section}). This class may have a wait-list "
						       + "system.");
						continue;
					}

					// Make sure the button can be clicked on. 
					if (enrollButton[0].GetAttribute("aria-disabled") == "true")
					{
						Log(1, $"Unable to enroll in {courseDeptId} ({section}). You're either enrolled in this class "
						       + "or your enrollment time has passed.");
						continue;
					}

					// click on the enroll button 
					enrollButton[0].Click();

					// Wait for the popup to appear. We know the popup appears when parts of the UI is 
					// "blocked off" due to a popup. 
					Wait(driver, x => GetPopUpElement(x as ChromeDriver)
							.Any(a => a.GetAttribute("style").Contains("display: block;")),
						TimeSpan.FromSeconds(10));

					var possiblePopups = GetPopUpElement(driver);
					// a popup should now appear. This is where we can attempt to confirm said enrollment. 
					// First, check if a confirm button exists. If it doesn't, then an error occurred and we can ex
					var confirmButton = possiblePopups
						.FirstOrDefault(x => x.GetAttribute("style").Contains("display: block;"))?
						.FindElements(By.ClassName("ui-button-text"))?
						.FirstOrDefault(x => x.Text == "Confirm");
					// The confirm button doesn't exist.
					if (confirmButton is null)
					{
						Log(1, $"Unable to enroll in {courseDeptId} ({section}). Do you have permission to "
						       + "enroll in this course?");
						var closeButton = GetPopUpElement(driver)
							.FirstOrDefault(x => x.GetAttribute("style").Contains("display: block;"))?
							.FindElements(By.Id("dialog-after-action-close"));
						closeButton?.FirstOrDefault()?.Click();
						continue;
					}

					confirmButton.Click();
					classesEnrolledIn.Add(section);
					Log(0, $"Successfully added section ID {section} to your schedule!");
					await Task.Delay(500);
					var popupBox = driver.FindElementsById("dialog-after-action");
					if (popupBox.Any())
					{
						try
						{
							// TODO confirm that the button exists 
							var emailButton = driver.FindElementById("dialog-after-action-email");
							emailButton.Click();
							Wait(driver, x => x.FindElements(By.Id("dialog-msg-close")).Count != 0,
								TimeSpan.FromSeconds(10));
							var closeEmailConfirmButton = driver.FindElementById("dialog-msg-close");
							closeEmailConfirmButton.Click();
							Console.WriteLine("\tConfirmation Email Sent.");
						}
						catch (Exception)
						{
							try
							{
								driver.FindElementById("dialog-after-action-close").Click();
							}
							catch (Exception)
							{
								// ignore it 
							}
						}
					}

					if (classesEnrolledIn.Count >= args.MaxClassesToEnroll)
						goto outLoop;
					Console.WriteLine();
				}
			}

			outLoop:
			Log(0, $"Successfully Enrolled In {classesEnrolledIn.Count} Classes.");
			Log(0, $"Classes: {string.Join(", ", classesEnrolledIn)}");
		}
	}
}