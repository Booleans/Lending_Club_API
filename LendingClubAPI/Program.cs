﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LendingClubAPI.Classes;
using Newtonsoft.Json;

namespace LendingClubAPI
{
    internal class Program
    {
        public static string latestLoansUrl;

        private static void Main(string[] args)
        {
            var activeAccounts = InstantiateAccounts();

            // Use a stopwatch to terminate code after a certain duration.
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            Parallel.ForEach(activeAccounts, investableAccount =>
            {
                // We only need to search for loans if the available balance >= minimum investment amount. 
                if (investableAccount.availableCash < investableAccount.amountToInvestPerLoan)
                {
                    return;
                }

                while (stopwatch.ElapsedMilliseconds < 120000 && investableAccount.availableCash >= investableAccount.amountToInvestPerLoan)
                {
                    // If this is the first time retrieving listed loans, retrieve all.
                    // Retrieve only new loans for subsequent loops. 
                    if (investableAccount.getAllLoans)
                    {
                        latestLoansUrl = "https://api.lendingclub.com/api/investor/v1/loans/listing?showAll=true";
                        investableAccount.getAllLoans = false;
                    }
                    else
                    {
                        latestLoansUrl = "https://api.lendingclub.com/api/investor/v1/loans/listing?showAll=false";
                    }

                    // Retrieve the latest offering of loans on the platform.
                    NewLoans latestListedLoans = GetNewLoansFromJson(RetrieveJsonString(latestLoansUrl, investableAccount.authorizationToken));

                    // Filter the new loans based off of my criteria. 
                    var filteredLoans = FilterNewLoans(latestListedLoans.loans, investableAccount);

                    // We only need to build an order if filteredLoan is not null.
                    if (!filteredLoans.Any())
                    {
                        // Wait one second before retrieving loans again if there are no loans passing the filter. 
                        Thread.Sleep(1000);
                        continue;
                    }

                    // Create a new order to purchase the filtered loans. 
                    Order order = BuildOrder(filteredLoans, investableAccount.amountToInvestPerLoan, investableAccount.investorID);

                    string output = JsonConvert.SerializeObject(order);

                    var orderResponse = JsonConvert.DeserializeObject<CompleteOrderConfirmation>(SubmitOrder(investableAccount.submitOrderUrl, output, investableAccount.authorizationToken));

                    var orderConfirmations = orderResponse.orderConfirmations.AsEnumerable();

                    var loansPurchased = (from confirmation in orderConfirmations
                                          where confirmation.investedAmount >= 0
                                          select confirmation.loanId);

                    // Add purchased loans to the list of loan IDs owned. 
                    investableAccount.loanIDsOwned.AddRange(loansPurchased);

                    // Subtract successfully invested loans from account balance.
                    investableAccount.availableCash -= loansPurchased.Count() * investableAccount.amountToInvestPerLoan;
                }

            });

            Console.ReadLine();
        }

        public static string RetrieveJsonString(string myURL, string authorizationToken)
        {
            WebRequest wrGETURL = WebRequest.Create(myURL);
 
            // Read authorization token from file.
            wrGETURL.Headers.Add("Authorization:" + authorizationToken);
            wrGETURL.ContentType = "applicaton/json; charset=utf-8";

            var objStream = wrGETURL.GetResponse().GetResponseStream();

            using (StreamReader reader = new StreamReader(objStream))
            {
                // Return a string of the JSON.
                return reader.ReadToEnd();
            }

        }

        // Method to convert JSON into account balance.
        public static Account GetAccountFromJson(string inputJson)
        {
            Account accountDetails = JsonConvert.DeserializeObject<Account>(inputJson);
            return accountDetails;
        }

        public static NotesOwned GetLoansOwnedFromJson(string inputJson)
        {
            NotesOwned notesOwned = JsonConvert.DeserializeObject<NotesOwned>(inputJson);
            return notesOwned;
        }

        public static NewLoans GetNewLoansFromJson(string inputJson)
        {
            NewLoans newLoans = JsonConvert.DeserializeObject<NewLoans>(inputJson);
            return newLoans;
        }

        public static IEnumerable<Loan> FilterNewLoans(List<Loan> newLoans, Account accountToUse)
        {

            var filteredLoans = (from l in newLoans
                                 where l.annualInc >= 59900 &&
                                 (l.purpose == "debt_consolidation" || l.purpose == "credit_card") &&
                                 (l.inqLast6Mths == 0) &&
                                 (l.intRate >= 10.0) &&
                                 //(l.intRate <= 18.0) &&
                                 (l.term == 36) &&
                                 (accountToUse.loanGradesAllowed.Contains(l.grade)) &&
                                 (l.mthsSinceLastDelinq == null) &&
                                 (l.loanAmount <= 1.1*l.revolBal) &&
                                 (l.loanAmount >= .9*l.revolBal) &&
                                 (accountToUse.allowedStates.Contains(l.addrState.ToString())) &&
                                 (!accountToUse.loanIDsOwned.Contains(l.id))
                                 orderby l.intRate descending                                                            
                                 select l).Take(accountToUse.numberOfLoansToInvestIn);
            
            return filteredLoans;
        }

        public static Order BuildOrder(IEnumerable<Loan> loansToBuy, double amountToInvest, string accountNumber)
        {
            Order order = new Order {aid = (Int32.Parse(accountNumber))};

            List<LoanForOrder> loansToOrder = new List<LoanForOrder>();

            foreach (Loan loan in loansToBuy)
            {
                LoanForOrder buyLoan = new LoanForOrder
                {
                    loanId = loan.id,
                    requestedAmount = amountToInvest,
                    portfolioId = null
                };

                loansToOrder.Add(buyLoan);
            }

            order.orders = loansToOrder;
            return order;
        }

        public static string SubmitOrder(string postURL, string jsonToSubmit, string authorizationToken)
        {
            
            var httpWebRequest = (HttpWebRequest)WebRequest.Create(postURL);
            httpWebRequest.Headers.Add("Authorization:" + authorizationToken);
            httpWebRequest.ContentType = "application/json; charset=utf-8";
            httpWebRequest.Method = "POST";            

            using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
            {
                string json = jsonToSubmit;

                streamWriter.Write(json);
            }

            var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                var result = streamReader.ReadToEnd();
                return result;
            }
        }

        public static string[] CalculateAndSetAllowedStatesFromCsv(string CSVInputpath, double statePercentLimit, double totalAccountValue)
        {
            string allowedStatesFromCSV = File.ReadAllText(CSVInputpath);
            char[] delimiters = new char[] { '\r', '\n' };
            string[] allowedStates = allowedStatesFromCSV.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

            string[] stateAbbreviations = new string[] {
                                 "AK","AL","AR","AZ","CA",
                                 "CO","CT","DE","FL","GA",
                                 "HI","IA","ID","IL","IN",
                                 "KS","KY","LA","MA","MD",
                                 "ME","MI","MN","MO","MS",
                                 "MT","NC","ND","NE","NH",
                                 "NJ","NM","NV","NY","OH",
                                 "OK","OR","PA","RI","SC",
                                 "SD","TN","TX","UT","VA",
                                 "VT","WA","WI","WV","WY"};

            Dictionary<string, double> states = stateAbbreviations.ToDictionary(state => state, state => 0.0);

            // Skip the first line because it contains the row headings. 
            foreach (string note in allowedStates.Skip(1))
            {
                string stateOfNote = null;
                double principalRemainingOfNote = 0;

                var noteDetails = note.Split(',');

                // We need to make sure we are only using loans that are current. 
                bool isNoteCurrent = noteDetails.Any(detail => detail == "Current");

                // If the note is not current then skip to the next iteration of the foreach loop. 
                if (!isNoteCurrent) continue;

                stateOfNote = noteDetails.First(detail => stateAbbreviations.Contains(detail));

                principalRemainingOfNote = Double.Parse(noteDetails[10]);

                // Increase the principal value in that state.
                states[stateOfNote] += Math.Round(principalRemainingOfNote, 2);
            }

            // Sort the states in alphabetical order.
            var sortedStates = from k in states
                               where (k.Value <= statePercentLimit * totalAccountValue)
                               orderby k.Key
                               select k.Key;

            return sortedStates.ToArray();
        }

        public static List<Account> InstantiateAccounts()
        {
            // Need a list of active accounts if we are going to be running this code on multiple accounts.
            List<Account> activeAccounts = new List<Account>();

            // Find the directory of the project so we can use a relative path to the authorization token file. 
            string projectDirectory = Directory.GetParent(Directory.GetParent(Directory.GetCurrentDirectory()).ToString()).ToString();

            const string andrewAuthorizationTokenFilePath = @"C:\AndrewAuthorizationToken.txt";
            var andrewAuthorizationToken = File.ReadAllText(andrewAuthorizationTokenFilePath);

            // Store the Account object to get balance and outstanding principal.
            Account andrewTaxableAccount = GetAccountFromJson(RetrieveJsonString("https://api.lendingclub.com/api/investor/v1/accounts/1302864/summary", andrewAuthorizationToken));

            andrewTaxableAccount.authorizationToken = andrewAuthorizationToken;
            andrewTaxableAccount.statePercentLimit = 0.05;
            andrewTaxableAccount.amountToInvestPerLoan = 25.0;
            andrewTaxableAccount.loanGradesAllowed = new string[] { "B", "C", "D" };
            andrewTaxableAccount.authorizationTokenFilePath = @"C:\AndrewAuthorizationToken.txt";
            andrewTaxableAccount.notesFromCSVFilePath = projectDirectory + @"\notes_ext.csv";
            andrewTaxableAccount.allowedStates = CalculateAndSetAllowedStatesFromCsv(andrewTaxableAccount.notesFromCSVFilePath, andrewTaxableAccount.statePercentLimit, andrewTaxableAccount.accountTotal);
            andrewTaxableAccount.numberOfLoansToInvestIn = (int)(andrewTaxableAccount.availableCash / andrewTaxableAccount.amountToInvestPerLoan);
            andrewTaxableAccount.detailedNotesOwnedUrl = "https://api.lendingclub.com/api/investor/v1/accounts/" + andrewTaxableAccount.investorID + "/detailednotes";
            andrewTaxableAccount.accountSummaryUrl = "https://api.lendingclub.com/api/investor/v1/accounts/" + andrewTaxableAccount.investorID + "/summary";
            andrewTaxableAccount.submitOrderUrl = "https://api.lendingclub.com/api/investor/v1/accounts/" + andrewTaxableAccount.investorID + "/orders";
            andrewTaxableAccount.notesOwnedByAccount = GetLoansOwnedFromJson(RetrieveJsonString(andrewTaxableAccount.detailedNotesOwnedUrl, andrewTaxableAccount.authorizationToken));
            andrewTaxableAccount.loanIDsOwned = (from loan in andrewTaxableAccount.notesOwnedByAccount.myNotes.AsEnumerable()
                                                 select loan.loanId).ToList();

            activeAccounts.Add(andrewTaxableAccount);

            return activeAccounts;
        }
    }
}
