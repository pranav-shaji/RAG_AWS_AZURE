import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

import {
  Chart,
  ArcElement,
  BarElement,
  CategoryScale,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  Legend,
  PieController,
  BarController,
  LineController,
  ChartConfiguration
} from 'chart.js';

import { BaseChartDirective } from 'ng2-charts';

Chart.register(
  ArcElement,
  BarElement,
  CategoryScale,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  Legend,
  PieController,
  BarController,
  LineController
);

@Component({
  selector: 'app-pie-chart-message',
  standalone: true,
  imports: [
    CommonModule,
    BaseChartDirective
  ],
  templateUrl: './pie-chart-message.html',
  styleUrl: './pie-chart-message.css'
})
export class PieChartMessageComponent {

  @Input() chartType = 'pie-chart';

  @Input() labels: string[] = [];

  @Input() values: number[] = [];

  get type(): 'pie' | 'bar' | 'line' {

  const normalized =
    this.chartType
      ?.toLowerCase()
      ?.trim();

  if (
    normalized === 'bar-chart' ||
    normalized === 'barchart' ||
    normalized === 'bar'
  ) {
    return 'bar';
  }

  if (
    normalized === 'line-chart' ||
    normalized === 'linechart' ||
    normalized === 'line'
  ) {
    return 'line';
  }

  return 'pie';
}

  get data(): ChartConfiguration['data'] {
    return {
      labels: this.labels,
      datasets: [
        {
          label: this.type === 'pie' ? 'Share' : 'Value',
          data: this.values,
          backgroundColor: [
            '#2563eb',
            '#10b981',
            '#f59e0b',
            '#ef4444',
            '#8b5cf6',
            '#06b6d4',
            '#64748b',
            '#84cc16'
          ],
          borderColor: '#ffffff',
          borderWidth: this.type === 'pie' ? 2 : 1
        }
      ]
    };
  }

  get options(): ChartConfiguration['options'] {
    return {
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: {
          position: this.type === 'pie' ? 'bottom' : 'top'
        }
      },
      scales: this.type === 'pie'
        ? undefined
        : {
          y: {
            beginAtZero: true,
            ticks: {
              precision: 0
            }
          }
        }
    };
  }
}
