import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';

import 'app_ui.dart';

/// Smooth area line chart (used for the requests trend).
class AreaLineChart extends StatelessWidget {
  final List<double> values;
  final List<String> bottomLabels;
  final Color color;
  final Color textColor;
  final bool showAxis;
  final double height;
  const AreaLineChart({
    super.key,
    required this.values,
    this.bottomLabels = const [],
    this.color = kPrimary,
    this.textColor = kMuted,
    this.showAxis = false,
    this.height = 130,
  });

  @override
  Widget build(BuildContext context) {
    final maxV = (values.isEmpty ? 1.0 : values.reduce((a, b) => a > b ? a : b));
    final top = maxV <= 0 ? 4.0 : (maxV * 1.25);
    return SizedBox(
      height: height,
      child: LineChart(
        LineChartData(
          minY: 0,
          maxY: top,
          gridData: FlGridData(
            show: showAxis,
            drawVerticalLine: false,
            horizontalInterval: top / 3,
            getDrawingHorizontalLine: (_) => FlLine(color: textColor.withValues(alpha: 0.12), strokeWidth: 1),
          ),
          borderData: FlBorderData(show: false),
          titlesData: FlTitlesData(
            leftTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: showAxis,
                reservedSize: 28,
                interval: top / 3,
                getTitlesWidget: (v, _) => Text(v.toInt().toString(),
                    style: TextStyle(color: textColor, fontSize: 9)),
              ),
            ),
            rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
            topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
            bottomTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: bottomLabels.isNotEmpty,
                reservedSize: 22,
                interval: 1,
                getTitlesWidget: (v, _) {
                  final i = v.toInt();
                  if (i < 0 || i >= bottomLabels.length) return const SizedBox.shrink();
                  return Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(bottomLabels[i], style: TextStyle(color: textColor, fontSize: 9)),
                  );
                },
              ),
            ),
          ),
          lineBarsData: [
            LineChartBarData(
              spots: [for (var i = 0; i < values.length; i++) FlSpot(i.toDouble(), values[i])],
              isCurved: true,
              curveSmoothness: 0.35,
              color: color,
              barWidth: 3,
              dotData: const FlDotData(show: false),
              belowBarData: BarAreaData(
                show: true,
                gradient: LinearGradient(
                  begin: Alignment.topCenter,
                  end: Alignment.bottomCenter,
                  colors: [color.withValues(alpha: 0.35), color.withValues(alpha: 0.02)],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class DonutSlice {
  final double value;
  final Color color;
  DonutSlice(this.value, this.color);
}

/// Donut chart with a centered label.
class DonutChart extends StatelessWidget {
  final List<DonutSlice> slices;
  final String centerTop;
  final String centerBottom;
  final double size;
  const DonutChart({
    super.key,
    required this.slices,
    required this.centerTop,
    required this.centerBottom,
    this.size = 160,
  });

  @override
  Widget build(BuildContext context) {
    final total = slices.fold<double>(0, (s, e) => s + e.value);
    return SizedBox(
      width: size,
      height: size,
      child: Stack(alignment: Alignment.center, children: [
        PieChart(
          PieChartData(
            sectionsSpace: 2,
            centerSpaceRadius: size * 0.32,
            sections: total <= 0
                ? [PieChartSectionData(value: 1, color: const Color(0xFFE7ECF3), radius: size * 0.13, showTitle: false)]
                : slices
                    .map((s) => PieChartSectionData(value: s.value, color: s.color, radius: size * 0.13, showTitle: false))
                    .toList(),
          ),
        ),
        Column(mainAxisSize: MainAxisSize.min, children: [
          Text(centerTop, style: const TextStyle(fontSize: 22, fontWeight: FontWeight.bold, color: kInk)),
          Text(centerBottom, style: const TextStyle(fontSize: 10, color: kMuted)),
        ]),
      ]),
    );
  }
}

/// chart palette (consistent across screens)
const kChartColors = [
  kPrimary,
  kGreen,
  Color(0xFFF6C000),
  Color(0xFF22CCE2),
  Color(0xFF7239EA),
  Color(0xFFFF6F61),
];
