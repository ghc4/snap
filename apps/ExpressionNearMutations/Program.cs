﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using ExpressionLib;
using System.Diagnostics;
using System.Threading;

namespace ExpressionNearMutations
{
    class Program
    {

        class RegionalExpressionState
        {
            public int nRegionsIncluded = 0;
            public double minExpression = 100000;
            public double maxExpression = -100000;
            public double totalExpression = 0;

            public double minMeanExpression = 100000;
            public double maxMeanExpression = -1;
            public double totalMeanExpression = 0;

            public void AddExpression(double z, double mu)
            {
                nRegionsIncluded++;
                totalExpression += z;
                minExpression = Math.Min(minExpression, z);
                maxExpression = Math.Max(maxExpression, z);

                totalMeanExpression += mu;
                minMeanExpression = Math.Min(minMeanExpression, mu);
                maxMeanExpression = Math.Max(maxMeanExpression, mu);
            }
        }


        class GeneExpression 
        {
            static GeneExpression()
            {
                regionSizeByRegionSizeIndex[0] = 0;
                regionSizeByRegionSizeIndex[1] = 1000;
                for (int i = 2; i < nRegionSizes; i++)
                {
                    regionSizeByRegionSizeIndex[i] = regionSizeByRegionSizeIndex[i - 1] * 2;
                }

                comparer = StringComparer.OrdinalIgnoreCase;
            }

            public GeneExpression(ExpressionTools.Gene gene_) 
            {
                gene = gene_;

                for (int sizeIndex = 0; sizeIndex < nRegionSizes; sizeIndex++)
                {
                    regionalExpressionState[sizeIndex] = new RegionalExpressionState();
                }
            }

            public void AddRegionalExpression(int chromosomeOffset, double z, double mu)
            {
                int distance;
                if (chromosomeOffset >= gene.minOffset && chromosomeOffset <= gene.maxOffset)
                {
                    distance = 0;
                }
                else if (chromosomeOffset < gene.minOffset)
                {
                    distance = gene.minOffset - chromosomeOffset;
                }
                else
                {
                    distance = chromosomeOffset - gene.maxOffset;
                }


                if (0 == distance)
                {
                    regionalExpressionState[0].AddExpression(z, mu);
                }
                else
                {
                    for (int sizeIndex = nRegionSizes - 1; sizeIndex > 0; sizeIndex--)  // Don't do 0, so we exclude the gene from the surronding region
                    {
                        if (regionSizeByRegionSizeIndex[sizeIndex] < distance)
                        {
                            break;
                        }

                        regionalExpressionState[sizeIndex].AddExpression(z, mu);
                    }
                }
            }

            public static int CompareByGeneName(GeneExpression a, GeneExpression b)
            {
                return comparer.Compare(a.gene.hugo_symbol, b.gene.hugo_symbol);
            }

            public const int nRegionSizes = 20;    // Because we have 0 (in the gene), this range is 2^(20 - 2) * 1000 = 262 Mbases on either side, i.e., the entire chromosome
            public static readonly int[] regionSizeByRegionSizeIndex = new int[nRegionSizes];

            public RegionalExpressionState[] regionalExpressionState = new RegionalExpressionState[nRegionSizes]; // Dimension is log2(regionSize) - 1

            public ExpressionTools.Gene gene;
            public int mutationCount = 0;
            public static StringComparer comparer;
        }



        static Dictionary<string, ExpressionTools.MutationMap> mutations;

        static void ProcessParticipants(List<string> participantsToProcess, bool forAlleleSpecificExpression)
        {
            var timer = new Stopwatch();

            string participantId;

            while (true)
            {
                lock (participantsToProcess)
                {
                    if (participantsToProcess.Count() == 0)
                    {
                        return;
                    }

                    participantId = participantsToProcess[0];
                    participantsToProcess.RemoveAt(0);
                }

                timer.Reset();
                timer.Start();

                if (!experimentsByParticipant.ContainsKey(participantId))
                {
                    Console.WriteLine("Couldn't find experiment for participant ID " + participantId);
                    continue;
                }

                var experiment = experimentsByParticipant[participantId];

                var inputFilename = forAlleleSpecificExpression ? experiment.NormalDNAAnalysis.annotatedSelectedVariantsFileName : experiment.TumorDNAAnalysis.regionalExpressionFileName;

                if (inputFilename == "")
                {
                    Console.WriteLine("Participant " + participantId + " doesn't have an input file yet.");
                    continue;
                }

                var mutationsForThisReference = mutations[experiment.maf[0].ReferenceClass()];

                var geneExpressions = new Dictionary<string, GeneExpression>();    
                foreach (var maf in experiment.maf)
                {
                    if (maf.Variant_classification == "Silent")
                    {
                        continue;
                    }

                    if (!mutationsForThisReference.genesByName.ContainsKey(maf.Hugo_symbol)) {
                        //
                        // Probably an inconsistent gene.  Skip it.
                        //
                        continue;
                    }

                    if (!geneExpressions.ContainsKey(maf.Hugo_symbol))
                    {
                        geneExpressions.Add(maf.Hugo_symbol, new GeneExpression(mutationsForThisReference.genesByName[maf.Hugo_symbol]));
 
                    }
 
                    geneExpressions[maf.Hugo_symbol].mutationCount++;
                }

                var reader = new StreamReader(inputFilename);

                var headerLine = reader.ReadLine();
                if (null == headerLine)
                {
                    Console.WriteLine("Empty input file " + inputFilename);
                    continue;
                }

                string line;
                int lineNumber = 1;
                if (!forAlleleSpecificExpression)
                {
                    if (headerLine.Substring(0, 20) != "RegionalExpression v")
                    {
                        Console.WriteLine("Corrupt header line in file '" + inputFilename + "', line: " + headerLine);
                        continue;
                    }

                    if (headerLine.Substring(20, 1) != "3")
                    {
                        Console.WriteLine("Unsupported version in file '" + inputFilename + "', header line: " + headerLine);
                        continue;
                    }
                    line = reader.ReadLine();   // The NumContigs line, which we just ignore
                    line = reader.ReadLine();   // The column header line, which we just ignore

                    if (null == line)
                    {
                        Console.WriteLine("Truncated file '" + inputFilename + "' ends after header line.");
                        continue;
                    }

                    lineNumber = 3;
                }

                bool seenDone = false;
                while (null != (line = reader.ReadLine()))
                {
                    lineNumber++;

                    if (seenDone)
                    {
                        Console.WriteLine("Saw data after **done** in file " + inputFilename + "', line " + lineNumber + ": " + line);
                        break;
                    }

                    if (line == "**done**")
                    {
                        seenDone = true;
                        continue;
                    }

                    var fields = line.Split('\t');
                    if (fields.Count() != (forAlleleSpecificExpression ? 20 : 13))
                    {
                        Console.WriteLine("Badly formatted data line in file '" + inputFilename + "', line " + lineNumber + ": " + line);
                        break;
                    }

                    string chromosome;
                    int offset;

                    // For allele-specific expression
                    double nMatchingReferenceDNA = 0;
                    double nMatchingVariantDNA = 0;
                    double nMatchingReferenceRNA = 0;
                    double nMatchingVariantRNA = 0;

                    // for regional expression
                    double z = 0;
                    double mu = 0;
 
                    try {
                        if (forAlleleSpecificExpression) {
                            chromosome = fields[0].ToLower();
                            offset = Convert.ToInt32(fields[1]);
                            nMatchingReferenceDNA = Convert.ToInt32(fields[12]);
                            nMatchingVariantDNA = Convert.ToInt32(fields[13]);
                            nMatchingReferenceRNA = Convert.ToInt32(fields[16]);
                            nMatchingVariantRNA = Convert.ToInt32(fields[17]);

                            if (!mutationsForThisReference.genesByChromosome.ContainsKey(chromosome))
                            {
                                //
                                // Try reversing the "chr" state of the chromosome.
                                //

                                if (chromosome.Count() > 3 && chromosome.Substring(0, 3) == "chr")
                                {
                                    chromosome = chromosome.Substring(3);
                                }
                                else
                                {
                                    chromosome = "chr" + chromosome;
                                }
                            }
                        } else {
                            chromosome = fields[0];
                            offset = Convert.ToInt32(fields[1]);
                            z = Convert.ToDouble(fields[11]);
                            mu = Convert.ToDouble(fields[12]);

                            int nBasesExpressedWithBaselineExpression = Convert.ToInt32(fields[3]);
                            int nBasesUnexpressedWithBaselineExpression = Convert.ToInt32(fields[7]);

                            if (0 == nBasesExpressedWithBaselineExpression && 0 == nBasesUnexpressedWithBaselineExpression)
                            {
                                //
                                // No baseline expression for this region, skip it.
                                //
                                continue;
                            }
                        }
                    }
                    catch (FormatException)
                    {
                        Console.WriteLine("Format exception parsing data line in file '" + inputFilename + "', line " + lineNumber + ": " + line);
                        break;
                    }

                    if (mutationsForThisReference.genesByChromosome.ContainsKey(chromosome) && 
                        (!forAlleleSpecificExpression ||                                    // We only keep samples for allele specific expression if they meet certain criteria, to wit:
                            nMatchingReferenceDNA + nMatchingVariantDNA >= 10 &&            // We have at least 10 DNA reads
                            nMatchingReferenceRNA + nMatchingVariantRNA >= 10 &&            // We have at least 10 RNA reads
                            nMatchingReferenceDNA * 3 >= nMatchingVariantDNA * 2 &&         // It's not more than 2/3 variant DNA
                            nMatchingVariantDNA * 3 >= nMatchingReferenceDNA * 2))          // It's not more than 2/3 reference DNA
                    {
                        foreach (var gene in mutationsForThisReference.genesByChromosome[chromosome])
                        {
                            if (!geneExpressions.ContainsKey(gene.hugo_symbol))
                            {
                                geneExpressions.Add(gene.hugo_symbol, new GeneExpression(gene));
                            }

                            if (forAlleleSpecificExpression) 
                            { 
                                double rnaFraction = nMatchingVariantRNA / (nMatchingReferenceRNA + nMatchingVariantRNA);

                                //
                                // Now convert to the amount of allele-specific expression.  50% is no ASE, while 0 or 100% is 100% ASE.
                                //
                                double alleleSpecificExpression = Math.Abs(rnaFraction * 2.0 - 1.0);

                                geneExpressions[gene.hugo_symbol].AddRegionalExpression(offset, alleleSpecificExpression, 0 /* no equivalent of mu for ASE */);
                            }
                            else
                            {
                                geneExpressions[gene.hugo_symbol].AddRegionalExpression(offset, z, mu);
                            }
                        }
                    }
 
                }

                if (!seenDone)
                {
                    Console.WriteLine("Truncated input file " + inputFilename);
                    continue;
                }

                //
                // Write the output file.
                //
                string directory = ExpressionTools.GetDirectoryPathFromFullyQualifiedFilename(inputFilename);
                string analysisId = ExpressionTools.GetAnalysisIdFromPathname(inputFilename);
                if ("" == directory || "" == analysisId) {
                    Console.WriteLine("Couldn't parse input pathname, which is supposed to be absolute and include an analysis ID: " + inputFilename);
                    continue;
                }

                var outputFilename = directory + analysisId + (forAlleleSpecificExpression ? ExpressionTools.alleleSpecificGeneExpressionExtension : ExpressionTools.geneExpressionExtension);

                var outputFile = new StreamWriter(outputFilename);

                outputFile.WriteLine("ExpressionNearMutations v2.1 " + participantId + (forAlleleSpecificExpression ? " -a" : ""));
                outputFile.Write("Gene name\tnon-silent mutation count");
                for (int sizeIndex = 0; sizeIndex < GeneExpression.nRegionSizes; sizeIndex++)
                {
                    outputFile.Write("\t" + GeneExpression.regionSizeByRegionSizeIndex[sizeIndex] + "(ase)");
                }

                if (!forAlleleSpecificExpression) {
                    for (int sizeIndex = 0; sizeIndex < GeneExpression.nRegionSizes; sizeIndex++)
                    {
                        outputFile.Write("\t" + GeneExpression.regionSizeByRegionSizeIndex[sizeIndex] + "(mu)");
                    }
                }

                outputFile.WriteLine();

                var allExpressions = new List<GeneExpression>();
                foreach (var expressionEntry in geneExpressions)
                {
                    allExpressions.Add(expressionEntry.Value);
                }

                allExpressions.Sort(GeneExpression.CompareByGeneName);

                for (int i = 0; i < allExpressions.Count(); i++)
                {
                    outputFile.Write(allExpressions[i].gene.hugo_symbol + "\t" + allExpressions[i].mutationCount);

                    for (int sizeIndex = 0; sizeIndex < GeneExpression.nRegionSizes; sizeIndex++)
                    {
                        if (allExpressions[i].regionalExpressionState[sizeIndex].nRegionsIncluded != 0)
                        {
                            outputFile.Write("\t" + allExpressions[i].regionalExpressionState[sizeIndex].totalExpression / allExpressions[i].regionalExpressionState[sizeIndex].nRegionsIncluded);
                        }
                        else
                        {
                            outputFile.Write("\t*");
                        }
                    }

                    if (!forAlleleSpecificExpression) {
                        for (int sizeIndex = 0; sizeIndex < GeneExpression.nRegionSizes; sizeIndex++)
                        {
                            if (allExpressions[i].regionalExpressionState[sizeIndex].nRegionsIncluded != 0)
                            {
                                outputFile.Write("\t" + allExpressions[i].regionalExpressionState[sizeIndex].totalMeanExpression / allExpressions[i].regionalExpressionState[sizeIndex].nRegionsIncluded);
                            }
                            else
                            {
                                outputFile.Write("\t*");
                            }
                        }
                    }
                    outputFile.WriteLine();
                }

                outputFile.WriteLine("**done**");
                outputFile.Close();

                timer.Stop();
                lock (participantsToProcess)
                {
                    var nRemaining = participantsToProcess.Count();
                    Console.WriteLine("Processed participant " + participantId + " in " + (timer.ElapsedMilliseconds + 500) / 1000 + "s.  " + nRemaining + " remain" + ((1 == nRemaining) ? "s" : "") + " queued.");
                }
            }
        }

        static List<ExpressionTools.Experiment> experiments;
        static Dictionary<string, ExpressionTools.Participant> participants;
        static Dictionary<string, ExpressionTools.Experiment> experimentsByParticipant = new Dictionary<string, ExpressionTools.Experiment>();

        static void Main(string[] args)
        {
            if (args.Count() == 0 || args.Count() == 1 && args[0] == "-a")
            {
                Console.WriteLine("usage: ExpressionNearMutations {-a} <participantIdsToProcess>");
                Console.WriteLine("-a means to use allele-specific expression rather than total expression.");
                return;
            }

            bool forAlleleSpecificExpression = args[0] == "-a";

            Stopwatch timer = new Stopwatch();
            timer.Start();

            List<string> excludedAnalyses;
            Dictionary<string, ExpressionTools.TCGARecord> tcgaRecords;
            Dictionary<string, string> sampleToParticipantIDMap;
            Dictionary<string, ExpressionTools.Sample> allSamples;

            ExpressionTools.LoadStateFromExperimentsFile(out excludedAnalyses, out tcgaRecords, out sampleToParticipantIDMap, out participants, out experiments, out allSamples);

            timer.Stop();
            Console.WriteLine("Loaded " + experiments.Count() + " experiments with MAFs in " + (timer.ElapsedMilliseconds + 500) / 1000 + "s");

            //
            // Now build the map of mutations by gene.
            //

            timer.Reset();
            timer.Start();

            mutations = ExpressionTools.GenerateMutationMapFromExperiments(experiments, experimentsByParticipant);

            timer.Stop();
            Console.WriteLine("Loaded mutations in " + mutations["hg19"].Count() + " genes in " + (timer.ElapsedMilliseconds + 500) / 1000 + "s.");

            var participantsToProcess = new List<string>();
            for (int i = forAlleleSpecificExpression ? 1 : 0; i < args.Count(); i++ )
            {
                participantsToProcess.Add(args[i]);
            }

            //
            // Process the runs in parallel
            //
            int totalNumberOfExperiments = experiments.Count();
            timer.Reset();
            timer.Start();

            var threads = new List<Thread>();
            for (int i = 0; i < /*Environment.ProcessorCount*/ 1; i++)
            {
                threads.Add(new Thread(() => ProcessParticipants(participantsToProcess, forAlleleSpecificExpression)));
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            timer.Stop();
            Console.WriteLine("Processed " + (args.Count() - (forAlleleSpecificExpression ? 1 : 0)) + " experiments in " + (timer.ElapsedMilliseconds + 500) / 1000 + " seconds");
        }
    }
}