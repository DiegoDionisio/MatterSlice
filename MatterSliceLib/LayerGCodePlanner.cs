﻿/*
This file is part of MatterSlice. A commandline utility for
generating 3D printing GCode.

Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using MSClipperLib;
using System;
using System.Collections.Generic;

namespace MatterHackers.MatterSlice
{
	using MatterHackers.VectorMath;
	using Pathfinding;
	using QuadTree;
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	// The GCodePlanner class stores multiple moves that are planned.
	// It facilitates the avoidCrossingPerimeters to keep the head inside the print.
	// It also keeps track of the print time estimate for this planning so speed adjustments can be made for the minimum-layer-time.
	public class LayerGCodePlanner
	{
		private int currentExtruderIndex;

		private bool forceRetraction;

		private GCodeExport gcodeExport;

		private PathFinder lastValidPathFinder;
		private PathFinder pathFinder;
		private List<GCodePath> paths = new List<GCodePath>();

		private double perimeterStartEndOverlapRatio;

		private long retractionMinimumDistance_um;

		public double LayerTime { get; private set; } = 0;

		private GCodePathConfig travelConfig;

		private ConfigSettings config;

		public LayerGCodePlanner(ConfigSettings config, GCodeExport gcode, int travelSpeed, long retractionMinimumDistance_um, double perimeterStartEndOverlap = 0)
		{
			this.config = config;

			this.gcodeExport = gcode;
			travelConfig = new GCodePathConfig("travelConfig", "travel");
			travelConfig.SetData(travelSpeed, 0);

			LastPosition = gcode.GetPositionXY();
			forceRetraction = false;
			currentExtruderIndex = gcode.GetExtruderIndex();
			this.retractionMinimumDistance_um = retractionMinimumDistance_um;

			this.perimeterStartEndOverlapRatio = Math.Max(0, Math.Min(1, perimeterStartEndOverlap));
		}

		public long CurrentZ
		{
			get => gcodeExport.CurrentZ_um;
			set => gcodeExport.CurrentZ_um = value;
		}

		public IntPoint LastPosition { get; private set; }

		public PathFinder PathFinder
		{
			get => pathFinder;
			set
			{
				if (value != null
					&& lastValidPathFinder != value)
				{
					lastValidPathFinder = value;
				}

				pathFinder = value;
			}
		}

		public static GCodePath TrimGCodePathEnd(GCodePath inPath, long targetDistance)
		{
			var path = new GCodePath(inPath);
			// get a new trimmed polygon
			path.Polygon = path.Polygon.TrimEnd(targetDistance);

			return path;
		}

		public (double fixedTime, double variableTime, double totalTime) GetLayerTimes()
		{
			IntPoint lastPosition = gcodeExport.GetPosition();
			double fixedTime = 0.0;
			double variableTime = 0.0;

			foreach (var path in paths)
			{
				for (int pointIndex = 0; pointIndex < path.Polygon.Count; pointIndex++)
				{
					IntPoint currentPosition = path.Polygon[pointIndex];

					double thisTime = (lastPosition - currentPosition).LengthMm() / (double)(path.Speed);

					thisTime = Estimator.GetSecondsForMovement((lastPosition - currentPosition).LengthMm(),
						path.Speed,
						config.MaxAcceleration,
						config.MaxVelocity,
						config.JerkVelocity) * config.PrintTimeEstimateMultiplier;

					if (PathCanAdjustSpeed(path))
					{
						variableTime += thisTime;
					}
					else
					{
						fixedTime += thisTime;
					}

					lastPosition = currentPosition;
				}
			}

			return (fixedTime, variableTime, fixedTime + variableTime);
		}

		private bool PathCanAdjustSpeed(GCodePath path)
		{
			return path.Config.LineWidthUM > 0 && path.Config.GCodeComment != "BRIDGE";
		}

		public void CorrectLayerTimeConsideringMinimumLayerTime()
		{
			var layerTimes = GetLayerTimes();

			if (layerTimes.totalTime < config.MinimumLayerTimeSeconds
				&& layerTimes.variableTime > 0.0)
			{
				var goalRatio = layerTimes.variableTime / (config.MinimumLayerTimeSeconds - layerTimes.fixedTime);
				var currentRatio = Math.Max(gcodeExport.LayerSpeedRatio - .1, goalRatio);
				do
				{
					foreach (var path in paths)
					{
						if (PathCanAdjustSpeed(path))
						{
							// change the speed of the extrusion
							var goalSpeed = path.Config.Speed * currentRatio;
							if (goalSpeed < path.Config.Speed)
							{
								path.Speed = Math.Max(config.MinimumPrintingSpeed, goalSpeed);
							}
						}
					}

					layerTimes = GetLayerTimes();
					currentRatio -= .01;
				} while (layerTimes.totalTime < config.MinimumLayerTimeSeconds
					&& currentRatio >= (gcodeExport.LayerSpeedRatio - .1)
					&& currentRatio >= .1);

				gcodeExport.LayerSpeedRatio = currentRatio;
			}
			else
			{
				gcodeExport.LayerSpeedRatio = 1;
			}

			this.LayerTime = GetLayerTimes().totalTime;
		}

		public void ForceRetract()
		{
			forceRetraction = true;
		}

		public int GetExtruder()
		{
			return currentExtruderIndex;
		}

		public void QueueExtrusionMove(IntPoint destination, GCodePathConfig config)
		{
			GetLatestPathWithConfig(config).Polygon.Add(new IntPoint(destination, CurrentZ));
			LastPosition = destination;

			// ValidatePaths();
		}

		public void QueuePolygon(Polygon polygon, int startIndex, GCodePathConfig config)
		{
			IntPoint currentPosition = polygon[startIndex];

			if (!config.Spiralize
				&& (LastPosition.X != currentPosition.X
				|| LastPosition.Y != currentPosition.Y))
			{
				QueueTravel(currentPosition);
			}

			if (config.ClosedLoop)
			{
				for (int positionIndex = 1; positionIndex < polygon.Count; positionIndex++)
				{
					IntPoint destination = polygon[(startIndex + positionIndex) % polygon.Count];
					QueueExtrusionMove(destination, config);
					currentPosition = destination;
				}

				// We need to actually close the polygon so go back to the first point
				if (polygon.Count > 2)
				{
					QueueExtrusionMove(polygon[startIndex], config);
				}
			}
			else // we are not closed
			{
				if (startIndex == 0)
				{
					for (int positionIndex = 1; positionIndex < polygon.Count; positionIndex++)
					{
						IntPoint destination = polygon[positionIndex];
						QueueExtrusionMove(destination, config);
						currentPosition = destination;
					}
				}
				else
				{
					for (int positionIndex = polygon.Count - 1; positionIndex >= 1; positionIndex--)
					{
						IntPoint destination = polygon[(startIndex + positionIndex) % polygon.Count];
						QueueExtrusionMove(destination, config);
						currentPosition = destination;
					}
				}
			}
		}

		/// <summary>
		/// Ensure the layer has the correct minimum fan speeds set
		/// by applying speed corrections for minimum layer times.
		/// </summary>
		/// <param name="layerIndex"></param>
		public void FinalizeLayerFanSpeeds(int layerIndex)
		{
			CorrectLayerTimeConsideringMinimumLayerTime();
			int layerFanPercent = GetFanPercent(layerIndex, gcodeExport);
			foreach (var fanSpeed in queuedFanSpeeds)
			{
				fanSpeed.FanPercent = Math.Max(fanSpeed.FanPercent, layerFanPercent);
			}
		}

		private int GetFanPercent(int layerIndex, GCodeExport gcodeExport)
		{
			if (layerIndex < config.FirstLayerToAllowFan)
			{
				// Don't allow the fan below this layer
				return 0;
			}

			var minFanSpeedLayerTime = Math.Max(config.MinFanSpeedLayerTime, config.MaxFanSpeedLayerTime);
			// check if the layer time is slow enough that we need to turn the fan on
			if (this.LayerTime < minFanSpeedLayerTime)
			{
				if (config.MaxFanSpeedLayerTime >= minFanSpeedLayerTime)
				{
					// the max always comes on first so just return the max speed
					return config.FanSpeedMaxPercent;
				}

				// figure out how much to turn it on
				var amountSmallerThanMin = Math.Max(0, minFanSpeedLayerTime - this.LayerTime);
				var timeToMax = Math.Max(0, minFanSpeedLayerTime - config.MaxFanSpeedLayerTime);

				double ratioToMaxSpeed = 0;
				if (timeToMax > 0)
				{
					ratioToMaxSpeed = Math.Min(1, amountSmallerThanMin / timeToMax);
				}

				return config.FanSpeedMinPercent + (int)(ratioToMaxSpeed * (config.FanSpeedMaxPercent - config.FanSpeedMinPercent));
			}
			else // we are going to slow turn the fan off
			{
				return 0;
			}
		}

		// We need to keep track of all the fan speeds we have queue so that we can set
		// the minimum fan speed for the layer after all the paths for the layer have been added.
		// We cannot calculate the minimum fan speed until the entire layer is queued and we then need to
		// go back to every queued fan speed and adjust it
		List<GCodePath> queuedFanSpeeds = new List<GCodePath>();

		public void QueueFanCommand(int fanSpeedPercent, GCodePathConfig config)
		{
			var path = GetNewPath(config);
			path.FanPercent = fanSpeedPercent;

			queuedFanSpeeds.Add(path);
		}

		public void QueuePolygons(Polygons polygons, GCodePathConfig config)
		{
			foreach (var polygon in polygons)
			{
				QueuePolygon(polygon, 0, config);
			}
		}

		public bool QueuePolygonByOptimizer(Polygon polygon, PathFinder pathFinder, GCodePathConfig config, int layerIndex)
		{
			PathOrderOptimizer orderOptimizer = new PathOrderOptimizer(LastPosition);
			orderOptimizer.AddPolygon(polygon);

			orderOptimizer.Optimize(pathFinder, layerIndex, config);

			for (int i = 0; i < orderOptimizer.bestIslandOrderIndex.Count; i++)
			{
				int polygonIndex = orderOptimizer.bestIslandOrderIndex[i];
				QueuePolygon(polygon, orderOptimizer.startIndexInPolygon[polygonIndex], config);
			}

			return true;
		}

		public bool QueuePolygonsByOptimizer(Polygons polygons, PathFinder pathFinder, GCodePathConfig config, int layerIndex)
		{
			if (polygons.Count == 0)
			{
				return false;
			}

			PathOrderOptimizer orderOptimizer = new PathOrderOptimizer(LastPosition);
			orderOptimizer.AddPolygons(polygons);

			orderOptimizer.Optimize(pathFinder, layerIndex, config);

			for (int i = 0; i < orderOptimizer.bestIslandOrderIndex.Count; i++)
			{
				int polygonIndex = orderOptimizer.bestIslandOrderIndex[i];
				QueuePolygon(polygons[polygonIndex], orderOptimizer.startIndexInPolygon[polygonIndex], config);
			}

			return true;
		}

		bool canAppendTravel = true;
		public void QueueTravel(IntPoint positionToMoveTo, bool forceUniquePath = false)
		{
			GCodePath path = GetLatestPathWithConfig(travelConfig, forceUniquePath || !canAppendTravel);
			canAppendTravel = !forceUniquePath;

			if (forceRetraction)
			{
				path.Retract = RetractType.Force;
				forceRetraction = false;
			}

			if (PathFinder != null)
			{
				Polygon pathPolygon = new Polygon();
				if (PathFinder.CreatePathInsideBoundary(LastPosition, positionToMoveTo, pathPolygon, true, gcodeExport.LayerIndex))
				{
					IntPoint lastPathPosition = LastPosition;
					long lineLength_um = 0;

					if (pathPolygon.Count > 0)
					{
						// we can stay inside so move within the boundary
						for (int positionIndex = 0; positionIndex < pathPolygon.Count; positionIndex++)
						{
							path.Polygon.Add(new IntPoint(pathPolygon[positionIndex], CurrentZ)
							{
								Width = 0
							});
							lineLength_um += (pathPolygon[positionIndex] - lastPathPosition).Length();
							lastPathPosition = pathPolygon[positionIndex];
						}

						// If the internal move is very long (> retractionMinimumDistance_um), do a retraction
						if (lineLength_um > retractionMinimumDistance_um)
						{
							path.Retract = RetractType.Requested;
						}
					}
					// else the path is good it just goes directly to the positionToMoveTo
				}
				else if ((LastPosition - positionToMoveTo).LongerThen(retractionMinimumDistance_um / 10))
				{
					// can't find a good path and moving more than a very little bit
					path.Retract = RetractType.Requested;
				}
			}

			// Always check if the distance is greater than the amount need to retract.
			if ((LastPosition - positionToMoveTo).LongerThen(retractionMinimumDistance_um))
			{
				path.Retract = RetractType.Requested;
			}

			path.Polygon.Add(new IntPoint(positionToMoveTo, CurrentZ)
			{
				Width = 0,
			});

			LastPosition = positionToMoveTo;

			//ValidatePaths();
		}

		public bool ToolChangeRequired(int extruder)
		{
			if (extruder == currentExtruderIndex)
			{
				return false;
			}

			return true;
		}

		public void SetExtruder(int extruder)
		{
			currentExtruderIndex = extruder;
		}

		public void WriteQueuedGCode(long layerThickness_um)
		{
			GCodePathConfig lastConfig = null;
			int extruderIndex = gcodeExport.GetExtruderIndex();

			for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
			{
				var path = paths[pathIndex];
				if (extruderIndex != path.ExtruderIndex)
				{
					extruderIndex = path.ExtruderIndex;
					gcodeExport.SwitchExtruder(extruderIndex);
				}
				else if (path.Retract != RetractType.None)
				{
					double timeOfMove = 0;

					if (path.Config.LineWidthUM == 0)
					{
						var lengthToStart = (gcodeExport.GetPosition() - path.Polygon[0]).Length();
						var lengthOfMove = lengthToStart + path.Polygon.PolygonLength();
						timeOfMove = lengthOfMove / 1000.0 / path.Speed;
					}

					gcodeExport.WriteRetraction(timeOfMove, path.Retract == RetractType.Force);
				}
				if (lastConfig != path.Config && path.Config != travelConfig)
				{
					gcodeExport.WriteComment("TYPE:{0}".FormatWith(path.Config.GCodeComment));
					lastConfig = path.Config;
				}
				if (path.FanPercent != -1)
				{
					gcodeExport.WriteFanCommand(path.FanPercent);
				}

				if (path.Polygon.Count == 1
					&& path.Config != travelConfig
					&& (gcodeExport.GetPositionXY() - path.Polygon[0]).ShorterThen(path.Config.LineWidthUM * 2))
				{
					//Check for lots of small moves and combine them into one large line
					IntPoint nextPosition = path.Polygon[0];
					int i = pathIndex + 1;
					while (i < paths.Count && paths[i].Polygon.Count == 1 && (nextPosition - paths[i].Polygon[0]).ShorterThen(path.Config.LineWidthUM * 2))
					{
						nextPosition = paths[i].Polygon[0];
						i++;
					}
					if (paths[i - 1].Config == travelConfig)
					{
						i--;
					}

					if (i > pathIndex + 2)
					{
						nextPosition = gcodeExport.GetPosition();
						for (int x = pathIndex; x < i - 1; x += 2)
						{
							long oldLen = (nextPosition - paths[x].Polygon[0]).Length();
							IntPoint newPoint = (paths[x].Polygon[0] + paths[x + 1].Polygon[0]) / 2;
							long newLen = (gcodeExport.GetPosition() - newPoint).Length();
							if (newLen > 0)
							{
								gcodeExport.WriteMove(newPoint, path.Speed, (int)(path.Config.LineWidthUM * oldLen / newLen));
							}

							nextPosition = paths[x + 1].Polygon[0];
						}

						long lineWidth_um = path.Config.LineWidthUM;
						if (paths[i - 1].Polygon[0].Width != 0)
						{
							lineWidth_um = paths[i - 1].Polygon[0].Width;
						}

						gcodeExport.WriteMove(paths[i - 1].Polygon[0], path.Speed, lineWidth_um);
						pathIndex = i - 1;
						continue;
					}
				}

				bool spiralize = path.Config.Spiralize;
				if (spiralize)
				{
					//Check if we are the last spiralize path in the list, if not, do not spiralize.
					for (int m = pathIndex + 1; m < paths.Count; m++)
					{
						if (paths[m].Config.Spiralize)
						{
							spiralize = false;
						}
					}
				}

				if (spiralize) // if we are still in spiralize mode
				{
					//If we need to spiralize then raise the head slowly by 1 layer as this path progresses.
					double totalLength = 0;
					long z = gcodeExport.GetPositionZ();
					IntPoint currentPosition = gcodeExport.GetPositionXY();
					for (int pointIndex = 0; pointIndex < path.Polygon.Count; pointIndex++)
					{
						IntPoint nextPosition = path.Polygon[pointIndex];
						totalLength += (currentPosition - nextPosition).LengthMm();
						currentPosition = nextPosition;
					}

					double length = 0.0;
					currentPosition = gcodeExport.GetPositionXY();
					for (int i = 0; i < path.Polygon.Count; i++)
					{
						IntPoint nextPosition = path.Polygon[i];
						length += (currentPosition - nextPosition).LengthMm();
						currentPosition = nextPosition;
						IntPoint nextExtrusion = path.Polygon[i];
						nextExtrusion.Z = (int)(z + layerThickness_um * length / totalLength + .5);
						gcodeExport.WriteMove(nextExtrusion, path.Speed, path.Config.LineWidthUM);
					}
				}
				else
				{
					var loopStart = gcodeExport.GetPosition();
					int pointCount = path.Polygon.Count;

					bool outerPerimeter = path.Config.GCodeComment == "WALL-OUTER";
					bool innerPerimeter = path.Config.GCodeComment == "WALL-INNER";
					bool perimeter = outerPerimeter || innerPerimeter;

					bool completeLoop = (pointCount > 0 && path.Polygon[pointCount - 1] == loopStart);
					bool trimmed = perimeter && completeLoop && perimeterStartEndOverlapRatio < 1;

					// This is test code to remove double drawn small perimeter lines.
					if (trimmed)
					{
						long targetDistance = (long)(path.Config.LineWidthUM * (1 - perimeterStartEndOverlapRatio));
						path = TrimGCodePathEnd(path, targetDistance);
						// update the point count after trimming
						pointCount = path.Polygon.Count;
					}

					for (int i = 0; i < pointCount; i++)
					{
						long lineWidth_um = path.Config.LineWidthUM;
						if (path.Polygon[i].Width != 0)
						{
							lineWidth_um = path.Polygon[i].Width;
						}

						gcodeExport.WriteMove(path.Polygon[i], path.Speed, lineWidth_um);
					}

					if (trimmed)
					{
						// go back to the start of the loop
						gcodeExport.WriteMove(loopStart, path.Speed, 0);

						var length = path.Polygon.PolygonLength(false);
						if (outerPerimeter
							&& config.CoastAtEndDistance_um > 0
							&& length > config.CoastAtEndDistance_um)
						{
							//gcodeExport.WriteRetraction
							var wipePoly = new Polygon(new IntPoint[] { loopStart });
							wipePoly.AddRange(path.Polygon);
							// then drive down it just a bit more to make sure we have a clean overlap
							var extraMove = wipePoly.CutToLength(config.CoastAtEndDistance_um);
							for (int i = 0; i < extraMove.Count; i++)
							{
								gcodeExport.WriteMove(extraMove[i], path.Speed, 0);
							}
						}
					}
				}
			}

			gcodeExport.UpdateLayerPrintTime();
		}

		private void ForceNewPathStart()
		{
			if (paths.Count > 0)
			{
				paths[paths.Count - 1].Done = true;
			}
		}

		private GCodePath GetLatestPathWithConfig(GCodePathConfig config, bool forceUniquePath = false)
		{
			if (!forceUniquePath
				&& paths.Count > 0
				&& paths[paths.Count - 1].Config == config
				&& !paths[paths.Count - 1].Done)
			{
				return paths[paths.Count - 1];
			}

			var path = GetNewPath(config);
			return path;
		}

		private GCodePath GetNewPath(GCodePathConfig config)
		{
			var path = new GCodePath
			{
				Retract = RetractType.None,
				ExtruderIndex = currentExtruderIndex,
				Done = false,
				Config = config
			};

			paths.Add(path);

			return path;
		}

		private void ValidatePaths()
		{
			bool first = true;
			var lastPosition = new IntPoint();
			for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
			{
				var path = paths[pathIndex];
				for (int polyIndex = 0; polyIndex < path.Polygon.Count; polyIndex++)
				{
					var position = path.Polygon[polyIndex];
					if (first)
					{
						first = false;
					}
					else
					{
						if (pathIndex == paths.Count - 1
							&& polyIndex == path.Polygon.Count - 1
							&& lastValidPathFinder != null
							&& !lastValidPathFinder.OutlineData.Polygons.PointIsInside((position + lastPosition) / 2))
						{
							// an easy way to get the path
							string startEndString = $"start:({position.X}, {position.Y}), end:({lastPosition.X}, {lastPosition.Y})";
							string outlineString = lastValidPathFinder.OutlineData.Polygons.WriteToString();
							long length = (position - lastPosition).Length();
						}
					}
					lastPosition = position;
				}
			}
		}
	}
}