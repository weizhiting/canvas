﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasCommon
{

    ///<summary>
    /// This class implements Density Clustering algorithm introduced in 
    /// Rodriguez, Alex, and Alessandro Laio. "Clustering by fast search and find of density peaks." Science 344.6191 (2014): 1492-1496.
    /// The principle class members are Centroids (Delta in paper) and Rho that are defined as follows:
    /// fiven distance matric d[i,j] and distanceThreshold, for each data point i compure 
    /// Rho(i) = total number of data points within distanceThreshold
    /// Centroids(i) = distance	of the closes data point of	higher density (min(d[i,j] for all j:=Rho(j)>Rho(i)))
    ///</summary>
    public class DensityClusteringModel
    {
        #region Members
        public List<SegmentInfo> Segments;
        public List<double?> Distance;
        public List<double> Centroids;
        public List<double> Rho;
        private List<double> CentroidsMAFs;
        private List<double> CentroidsCoverage;
        private double _coverageWeightingFactor;
        private double _knearestNeighbourCutoff;
        private double CentroidsCutoff;

        // parameters
        // RhoCutoff and CentroidsCutoff estimated from running density clustering on 70 HapMix tumour samples https://git.illumina.com/Bioinformatics/HapMix/
        // and visually inspecting validity of clusters
        public const double RhoCutoff = 2.0;
        public const int MaxClusterNumber = 7; // too many clusters suggest incorrect cluster partitioning 
        private const double NeighborRateLow = 0.02;
        private const double NeighborRateHigh = 0.03;
        #endregion


        public DensityClusteringModel(List<SegmentInfo> segments, double coverageWeightingFactor, double knearestNeighbourCutoff, double centroidsCutoff)
        {
            Segments = segments;
            _coverageWeightingFactor = coverageWeightingFactor;
            _knearestNeighbourCutoff = knearestNeighbourCutoff;
            CentroidsCutoff = centroidsCutoff;
        }

        /// <summary>
        /// Only use segments with non-null MAF values
        /// </summary>
        public int GetSegmentsForClustering(List<SegmentInfo> segments)
        {
            int segmentCounts = 0;
            foreach (SegmentInfo segment in segments)
            {
                if (segment.MAF >= 0)
                    segmentCounts++;
            }
            return segmentCounts;
        }

        /// <summary>
        /// Estimate cluster variance using cluster centroids identified by densityClustering function
        /// </summary>
        public List<double> GetCentroidsVariance(List<double> centroidsMAFs, List<double> centroidsCoverage, int nClusters)
        {
            List<double> clusterVariance = new List<double>();

            for (int clusterID = 0; clusterID < nClusters; clusterID++)
            {
                List<double> tmpDistance = new List<double>();
                foreach (SegmentInfo segment in this.Segments)
                {
                    if (segment.ClusterId.HasValue && clusterID + 1 == segment.ClusterId.Value)
                    {
                        tmpDistance.Add(GetEuclideanDistance(segment.Coverage, centroidsCoverage[clusterID], segment.MAF, centroidsMAFs[clusterID]));
                    }
                }
                clusterVariance.Add(tmpDistance.Average());
            }
            return clusterVariance;
        }

        /// <summary>
        /// Return cluster sizes
        /// </summary>
        public List<int> GetClustersSize(int nClusters)
        {
            List<int> clustersSize = Enumerable.Repeat(0, nClusters).ToList();

            foreach (SegmentInfo segment in this.Segments)
            {
                if (segment.ClusterId.HasValue && segment.ClusterId.Value > 0)
                {
                    clustersSize[segment.ClusterId.Value - 1] += 1;
                }
            }
            return clustersSize;
        }

        public List<double> GetCentroidsMAF()
        {
            return this.CentroidsMAFs;
        }

        public List<double> GetCentroidsCoverage()
        {
            return this.CentroidsCoverage;
        }



        /// <summary>
        /// Return the squared euclidean distance between (coverage, maf) and (coverage2, maf2) in scaled coverage/MAF space.
        /// https://en.wikipedia.org/wiki/Euclidean_distance
        /// </summary>
        private double GetEuclideanDistance(double coverage, double coverage2, double maf, double maf2)
        {
            double diff = (coverage - coverage2) * _coverageWeightingFactor;
            double distance = diff * diff;
            diff = maf - maf2;
            distance += diff * diff;
            return Math.Sqrt(distance);
        }

        /// <summary>
        /// neighborRate = average of number of elements of comb per row that are less than dc minus 1 divided by size
        /// </summary>
        public double EstimateDc(double neighborRateLow = NeighborRateLow, double neighborRateHigh = NeighborRateHigh)
        {

            double tmpLow = Double.MaxValue;
            double tmpHigh = Double.MinValue;
            foreach (double? element in this.Distance)
            {
                if (element.HasValue && element < tmpLow && element > 0)
                    tmpLow = (double)element;
                else if (element.HasValue && element > tmpHigh)
                    tmpHigh = (double)element;
            }

            double neighborRate = 0;
            int segmentsLength = GetSegmentsForClustering(this.Segments);
            double distanceThreshold = 0;
            while (true)
            {
                double neighborRateTmp = 0;
                distanceThreshold = (tmpLow + tmpHigh) / 2;
                foreach (double? element in this.Distance)
                    if (element.HasValue && element < distanceThreshold)
                        neighborRateTmp++;
                if (distanceThreshold > 0)
                    neighborRateTmp = neighborRateTmp + segmentsLength;

                neighborRate = (neighborRateTmp * 2 / segmentsLength - 1) / segmentsLength;

                if (neighborRate >= neighborRateLow && neighborRate <= neighborRateHigh)
                    break;

                if (neighborRate < neighborRateLow)
                {
                    tmpLow = distanceThreshold;
                }
                else
                {
                    tmpHigh = distanceThreshold;
                }
                neighborRateTmp = 0;
            }
            return distanceThreshold;
        }


        public void NonGaussianLocalDensity(double distanceThreshold)
        {
            int ncol = this.Segments.Count;
            int nrow = this.Segments.Count;
            this.Rho = new List<double>(nrow);
            for (int iRho = 0; iRho < nrow; iRho++)
                this.Rho.Add(0);
            int i = 0;
            for (int col = 0; col < ncol; col++)
            {
                for (int row = col + 1; row < nrow; row++)
                {
                    if (this.Distance[i].HasValue)
                    {
                        if (this.Distance[i] < distanceThreshold)
                        {
                            this.Rho[row] += 1;
                            this.Rho[col] += 1;
                        }
                    }
                    i++;
                }
            }
        }

        public void GaussianLocalDensity(double distanceThreshold)
        {
            int distanceLength = this.Distance.Count;
            List<double> half = new List<double>(distanceLength);
            for (int index = 0; index < distanceLength; index++)
                half.Add(0);
            for (int index = 0; index < distanceLength; index++)
            {
                if (this.Distance[index].HasValue)
                {
                    double combOver = (double)this.Distance[index] / distanceThreshold;
                    double negSq = Math.Pow(combOver, 2) * -1;
                    half[index] = Math.Exp(negSq);
                }
            }

            int ncol = this.Segments.Count;
            int nrow = this.Segments.Count;
            this.Rho = new List<double>(nrow);
            for (int iRho = 0; iRho < nrow; iRho++)
                this.Rho.Add(0);
            int i = 0;
            for (int col = 0; col < ncol; col++)
            {
                for (int row = col + 1; row < nrow; row++)
                {
                    double temp = half[i];
                    this.Rho[row] += temp;
                    this.Rho[col] += temp;
                    i++;
                }
            }
        }

        /// <summary>
        /// Compute lower triangle of the distance matrix stored by columns in a vector. If n is the number of observations, 
        /// then for i < j <= n, the dissimilarity between (column) i and (row) j is retrieved from index [n*i - i*(i+1)/2 + j-i+1]. 
        /// The length of the distance vector is n*(n-1)/2.
        /// </summary>
        public void EstimateDistance()
        {
            int segmentsLength = this.Segments.Count;
            this.Distance = new List<double?>(segmentsLength * (segmentsLength - 1) / 2);
            for (int col = 0; col < segmentsLength; col++)
            {
                for (int row = col + 1; row < segmentsLength; row++)
                {
                    double? tmp = null;
                    if (this.Segments[col].MAF >= 0 && this.Segments[row].MAF >= 0)
                        tmp = GetEuclideanDistance(this.Segments[col].Coverage, this.Segments[row].Coverage, this.Segments[col].MAF, this.Segments[row].MAF);
                    this.Distance.Add(tmp);
                }
            }
        }


        /// <summary>
        /// Estimate Centroids value as
        /// Centroids(i) = distance	of the closes data point of	higher density (min(d[i,j] for all j:=Rho(j)>Rho(i)))
        /// </summary>
        public void FindCentroids()
        {
            int segmentsLength = this.Segments.Count;
            this.Centroids = new List<double>(segmentsLength);
            for (int iCentroids = 0; iCentroids < segmentsLength; iCentroids++)
                this.Centroids.Add(0);
            List<double> maximum = new List<double>(segmentsLength);
            for (int imaximum = 0; imaximum < segmentsLength; imaximum++)
                maximum.Add(0);
            int i = 0;
            for (int col = 0; col < segmentsLength; col++)
            {
                for (int row = col + 1; row < segmentsLength; row++)
                {
                    if (!this.Distance[i].HasValue)
                    {
                        i++;
                        continue;
                    }
                    double newValue = (double)this.Distance[i];
                    double rhoRow = this.Rho[row];
                    double rhoCol = this.Rho[col];

                    if (rhoRow > rhoCol)
                    {
                        double CentroidsCol = this.Centroids[col];
                        if (newValue < CentroidsCol || CentroidsCol == 0)
                        {
                            this.Centroids[col] = newValue;
                        }
                    }
                    else if (newValue > maximum[col])
                    {
                        maximum[col] = newValue;
                    }

                    if (rhoCol > rhoRow)
                    {
                        double CentroidsRow = this.Centroids[row];
                        if (newValue < CentroidsRow || CentroidsRow == 0)
                        {
                            this.Centroids[row] = newValue;
                        }
                    }
                    else if (newValue > maximum[row])
                    {
                        maximum[row] = newValue;
                    }
                    i++;
                }
            }
            for (int j = 0; j < segmentsLength; j++)
            {
                if (this.Centroids[j] == 0)
                {
                    this.Centroids[j] = maximum[j];
                }
            }
        }


        /// <summary>
        /// Helper method for FindClusters
        /// </summary>
        private double? GetDistance(int segmentsLength, int tmpIndex, int runOrderIndex)
        {
            if (tmpIndex < runOrderIndex)
            {
                // the dissimilarity between (column) i and j is retrieved from index [n*i - i*(i+1)/2 + j-i-1].
                return this.Distance[segmentsLength * tmpIndex - (tmpIndex * (tmpIndex + 1)) / 2 + runOrderIndex - tmpIndex - 1];
            }
            else if (tmpIndex > runOrderIndex)
            {
                return this.Distance[segmentsLength * runOrderIndex - (runOrderIndex * (runOrderIndex + 1)) / 2 + tmpIndex - runOrderIndex - 1];
            }
            else
            {
                return null;
            }
        }


        public int FindClusters(double rhoCutoff = RhoCutoff)
        {
            CentroidsMAFs = new List<double>();
            CentroidsCoverage = new List<double>();
            int segmentsLength = this.Segments.Count;
            List<int> CentroidsIndex = new List<int>(segmentsLength);
            for (int segmentIndex = 0; segmentIndex < segmentsLength; segmentIndex++)
            {
                if (this.Rho[segmentIndex] > rhoCutoff && this.Centroids[segmentIndex] > CentroidsCutoff && this.Segments[segmentIndex].MAF >= 0)
                {
                    CentroidsIndex.Add(segmentIndex);
                    CentroidsMAFs.Add(this.Segments[segmentIndex].MAF);
                    CentroidsCoverage.Add(this.Segments[segmentIndex].Coverage);
                }
            }


            // sort list and return indices
            List<int> runOrder = new List<int>();
            var sortedScores = Rho.Select((x, i) => new KeyValuePair<double, int>(x, i)).OrderByDescending(x => x.Key).ToList();
            runOrder = sortedScores.Select(x => x.Value).ToList();

            foreach (int runOrderIndex in runOrder)
            {
                // set segment cluster value to the cluster centroid 
                if (CentroidsIndex.Contains(runOrderIndex))
                {
                    this.Segments[runOrderIndex].ClusterId = CentroidsIndex.FindIndex(x => x == runOrderIndex) + 1;
                }
                // set segment cluster value to the closest cluster segment 
                else
                {
                    double? tmpDistance = null;
                    double minDistance = Double.MaxValue;
                    int minRhoElementIndex = 0;
                    for (int tmpIndex = 0; tmpIndex < segmentsLength; tmpIndex++)
                    {
                        if (Rho[tmpIndex] > Rho[runOrderIndex] && this.Segments[tmpIndex].MAF >= 0)
                        {
                            tmpDistance = GetDistance(segmentsLength, tmpIndex, runOrderIndex);

                            if (tmpDistance.HasValue)
                            {
                                if (tmpDistance < minDistance)
                                {
                                    minRhoElementIndex = tmpIndex;
                                    minDistance = (double)tmpDistance;
                                }
                            }
                        }
                    }
                    // populate clusters
                    if (this.Segments[runOrderIndex].MAF >= 0)
                        this.Segments[runOrderIndex].ClusterId = this.Segments[minRhoElementIndex].ClusterId;
                    if (!this.Segments[runOrderIndex].ClusterId.HasValue || this.Segments[runOrderIndex].MAF < 0 || this.Segments[runOrderIndex].KnearestNeighbour > this._knearestNeighbourCutoff)
                        this.Segments[runOrderIndex].ClusterId = CanvasCommon.PloidyInfo.OutlierClusterFlag;
                }
            }
            return CentroidsIndex.Count;
        }
    }
}
