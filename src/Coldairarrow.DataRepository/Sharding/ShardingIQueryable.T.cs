﻿using Coldairarrow.Util;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Coldairarrow.DataRepository
{
    internal class ShardingQueryable<T> : IShardingQueryable<T> where T : class, new()
    {
        #region 构造函数

        public ShardingQueryable(IQueryable<T> source, DistributedTransaction transaction = null)
        {
            _source = source;
            _absTableType = source.ElementType;
            _absTableName = _absTableType.Name;
            _transaction = transaction;
        }

        #endregion

        #region 私有成员

        private DistributedTransaction _transaction { get; }
        private bool _openTransaction { get => _transaction?.OpenTransaction == true; }
        private Type _absTableType { get; }
        private string _absTableName { get; }
        private IQueryable<T> _source { get; set; }
        private Type MapTable(string targetTableName)
        {
            return DbModelFactory.GetEntityType(targetTableName);
        }
        private List<dynamic> GetStatisData(Func<IQueryable, dynamic> access, IQueryable newSource = null)
        {
            newSource = newSource ?? _source;
            var tables = ShardingConfig.Instance.GetReadTables(_absTableName);
            List<Task<dynamic>> tasks = new List<Task<dynamic>>();
            tables.ForEach(aTable =>
            {
                tasks.Add(Task.Run(() =>
                {
                    var targetTable = MapTable(aTable.tableName);
                    var targetIQ = DbFactory.GetRepository(aTable.conString, aTable.dbType).GetIQueryable(targetTable);
                    var newQ = newSource.ChangeSource(targetIQ);

                    return access(newQ);
                }));
            });
            Task.WaitAll(tasks.ToArray());

            return tasks.Select(x => x.Result).ToList();
        }
        private async Task<List<dynamic>> GetStatisDataAsync(Func<IQueryable, Task<dynamic>> access, IQueryable newSource = null)
        {
            newSource = newSource ?? _source;
            var tables = ShardingConfig.Instance.GetReadTables(_absTableName);
            List<Task<dynamic>> tasks = new List<Task<dynamic>>();
            tasks = tables.Select(aTable =>
            {
                var targetTable = MapTable(aTable.tableName);
                var targetIQ = DbFactory.GetRepository(aTable.conString, aTable.dbType).GetIQueryable(targetTable);
                var newQ = newSource.ChangeSource(targetIQ);

                return access(newQ);
            }).ToList();

            return (await Task.WhenAll(tasks)).ToList();
        }
        private async Task<List<TResult>> GetStatisDataGenericAsync<TResult>(Func<IQueryable, Task<TResult>> access, IQueryable newSource = null)
        {
            newSource = newSource ?? _source;
            var tables = ShardingConfig.Instance.GetReadTables(_absTableName);
            List<Task<TResult>> tasks = new List<Task<TResult>>();
            tasks = tables.Select(aTable =>
            {
                var targetTable = MapTable(aTable.tableName);
                var targetIQ = DbFactory.GetRepository(aTable.conString, aTable.dbType).GetIQueryable(targetTable);
                var newQ = newSource.ChangeSource(targetIQ);

                return access(newQ);
            }).ToList();

            return (await Task.WhenAll(tasks)).ToList();
        }

        private dynamic DynamicAverage(dynamic selector)
        {
            var list = GetStatisData(x => new KeyValuePair<int, dynamic>(x.Count(), Coldairarrow.Util.Extention.DynamicSum(x, selector))).Select(x => (KeyValuePair<int, dynamic>)x).ToList();
            var count = list.Sum(x => x.Key);
            dynamic sumList = list.Select(x => (decimal?)x.Value).ToList();
            dynamic sum = Enumerable.Sum(sumList);

            return (decimal?)sum / count;
        }
        private Task<TResult> DynamicAverageAsync<TResult>(Expression<Func<T, TResult>> selector)
        {
            //获取总数量
            var newSource = _source.Select(selector);
            var list = GetStatisDataGenericAsync(x => (,))
            var count = list.Sum(x => x.Key);
            dynamic sumList = list.Select(x => (decimal?)x.Value).ToList();
            dynamic sum = Enumerable.Sum(sumList);

            return (decimal?)sum / count;
        }

        private dynamic DynamicSum(dynamic selector)
        {
            return GetStatisData(x => Coldairarrow.Util.Extention.DynamicSum(x, selector)).Sum(x => (decimal?)x);
        }

        #endregion

        #region 外部接口

        public IShardingQueryable<T> Where(Expression<Func<T, bool>> predicate)
        {
            _source = _source.Where(predicate);

            return this;
        }
        public IShardingQueryable<T> Where(string predicate, params object[] values)
        {
            _source = _source.Where(predicate, values);

            return this;
        }
        public IShardingQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            _source = _source.OrderBy(keySelector);

            return this;
        }
        public IShardingQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            _source = _source.OrderByDescending(keySelector);

            return this;
        }
        public IShardingQueryable<T> OrderBy(string ordering, params object[] values)
        {
            _source = _source.OrderBy(ordering, values);

            return this;
        }
        public IShardingQueryable<T> Skip(int count)
        {
            _source = _source.Skip(count);

            return this;
        }
        public IShardingQueryable<T> Take(int count)
        {
            _source = _source.Take(count);

            return this;
        }
        public int Count()
        {
            return GetStatisData(x => x.Count()).Sum(x => (int)x);
        }
        public async Task<int> CountAsync()
        {
            var results = await GetStatisDataAsync(x => EntityFrameworkQueryableExtensions.CountAsync((dynamic)x));

            return results.Sum(x => (int)x);
        }
        public List<T> ToList()
        {
            return AsyncHelper.RunSync(() => ToListAsync());
        }
        public async Task<List<T>> ToListAsync()
        {
            //去除分页,获取前Take+Skip数量
            int? take = _source.GetTakeCount();
            int? skip = _source.GetSkipCount();
            skip = skip == null ? 0 : skip;
            var (sortColumn, sortType) = _source.GetOrderBy();
            var noPaginSource = _source.RemoveTake().RemoveSkip();
            if (!take.IsNullOrEmpty())
                noPaginSource = noPaginSource.Take(take.Value + skip.Value);

            //从各个分表获取数据
            var tables = ShardingConfig.Instance.GetReadTables(_absTableName);
            SynchronizedCollection<IRepository> dbs = new SynchronizedCollection<IRepository>();
            List<Task<List<T>>> tasks = tables.Select(aTable =>
            {
                var targetTable = MapTable(aTable.tableName);
                var targetDb = DbFactory.GetRepository(aTable.conString, aTable.dbType);
                if (_openTransaction)
                    _transaction.AddRepository(targetDb);
                else
                    dbs.Add(targetDb);
                var targetIQ = targetDb.GetIQueryable(targetTable);
                var newQ = noPaginSource.ChangeSource(targetIQ);
                return newQ
                    .Cast<object>()
                    .Select(x => x.ChangeType<T>())
                    .ToListAsync();
            }).ToList();
            List<T> all = new List<T>();
            (await Task.WhenAll(tasks.ToArray())).ToList().ForEach(x => all.AddRange(x));
            dbs.ForEach(x => x.Dispose());
            //合并数据
            var resList = all;
            if (!sortColumn.IsNullOrEmpty() && !sortType.IsNullOrEmpty())
                resList = resList.AsQueryable().OrderBy($"{sortColumn} {sortType}").ToList();
            if (!skip.IsNullOrEmpty())
                resList = resList.Skip(skip.Value).ToList();
            if (!take.IsNullOrEmpty())
                resList = resList.Take(take.Value).ToList();

            return resList;
        }
        public T FirstOrDefault()
        {
            var list = GetStatisData(x => x.FirstOrDefault());
            list.RemoveAll(x => x == null);
            return list.Select(x => Coldairarrow.Util.Extention.ChangeType<T>(x)).FirstOrDefault();
        }
        public async Task<T> FirstOrDefaultAsync()
        {
            var list = await GetStatisDataAsync(x => EntityFrameworkQueryableExtensions.FirstOrDefaultAsync((dynamic)x));
            list.RemoveAll(x => x.IsNullOrEmpty());
            return list.Select(x => (T)x.ChangeType<T>()).FirstOrDefault();
        }
        public List<T> GetPagination(Pagination pagination)
        {
            pagination.Total = Count();
            _source = _source.OrderBy($"{pagination.SortField} {pagination.SortType}");

            return Skip((pagination.PageIndex - 1) * pagination.PageRows).Take(pagination.PageRows).ToList();
        }
        public async Task<List<T>> GetPaginationAsync(Pagination pagination)
        {
            pagination.Total = Count();
            _source = _source.OrderBy($"{pagination.SortField} {pagination.SortType}");

            return await Skip((pagination.PageIndex - 1) * pagination.PageRows).Take(pagination.PageRows).ToListAsync();
        }
        public TResult Max<TResult>(Expression<Func<T, TResult>> selector)
        {
            return GetStatisData(x => x.Max(selector)).Max(x => (TResult)x);
        }
        public async Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector)
        {
            var newSource = _source.Select(selector);
            var results = await GetStatisDataAsync(x => EntityFrameworkQueryableExtensions.MaxAsync((dynamic)x), newSource);

            return results.Max(x => (TResult)x);
        }
        public TResult Min<TResult>(Expression<Func<T, TResult>> selector)
        {
            return GetStatisData(x => x.Min(selector)).Min(x => (TResult)x);
        }
        public async Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector)
        {
            var newSource = _source.Select(selector);
            var results = await GetStatisDataAsync(x => EntityFrameworkQueryableExtensions.MinAsync((dynamic)x), newSource);

            return results.Min(x => (TResult)x);
        }
        public double Average(Expression<Func<T, int>> selector)
        {
            return (double)DynamicAverage(selector);
        }
        public double? Average(Expression<Func<T, int?>> selector)
        {
            return (double?)DynamicAverage(selector);
        }
        public float Average(Expression<Func<T, float>> selector)
        {
            return (float)DynamicAverage(selector);
        }
        public float? Average(Expression<Func<T, float?>> selector)
        {
            return (float?)DynamicAverage(selector);
        }
        public double Average(Expression<Func<T, long>> selector)
        {
            return (double)DynamicAverage(selector);
        }
        public double? Average(Expression<Func<T, long?>> selector)
        {
            return (double?)DynamicAverage(selector);
        }
        public double Average(Expression<Func<T, double>> selector)
        {
            return (double)DynamicAverage(selector);
        }
        public double? Average(Expression<Func<T, double?>> selector)
        {
            return (double?)DynamicAverage(selector);
        }
        public decimal Average(Expression<Func<T, decimal>> selector)
        {
            return (decimal)DynamicAverage(selector);
        }
        public decimal? Average(Expression<Func<T, decimal?>> selector)
        {
            return (decimal?)DynamicAverage(selector);
        }
        public decimal Sum(Expression<Func<T, decimal>> selector)
        {
            return (decimal)DynamicSum(selector);
        }
        public decimal? Sum(Expression<Func<T, decimal?>> selector)
        {
            return (decimal?)DynamicSum(selector);
        }
        public double Sum(Expression<Func<T, double>> selector)
        {
            return (double)DynamicSum(selector);
        }
        public double? Sum(Expression<Func<T, double?>> selector)
        {
            return (double?)DynamicSum(selector);
        }
        public float Sum(Expression<Func<T, float>> selector)
        {
            return (float)DynamicSum(selector);
        }
        public float? Sum(Expression<Func<T, float?>> selector)
        {
            return (float?)DynamicSum(selector);
        }
        public int Sum(Expression<Func<T, int>> selector)
        {
            return (int)DynamicSum(selector);
        }
        public int? Sum(Expression<Func<T, int?>> selector)
        {
            return (int?)DynamicSum(selector);
        }
        public long Sum(Expression<Func<T, long>> selector)
        {
            return (long)DynamicSum(selector);
        }
        public long? Sum(Expression<Func<T, long?>> selector)
        {
            return (long?)DynamicSum(selector);
        }
        public bool Any(Expression<Func<T, bool>> predicate)
        {
            var newSource = _source.Where(predicate);
            return GetStatisData(x => x.Any(), newSource).Any(x => x == true);
        }





        public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate)
        {
            throw new NotImplementedException();
        }



        public async Task<double> AverageAsync(Expression<Func<T, int>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double?> AverageAsync(Expression<Func<T, int?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<float> AverageAsync(Expression<Func<T, float>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<float?> AverageAsync(Expression<Func<T, float?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double> AverageAsync(Expression<Func<T, long>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double?> AverageAsync(Expression<Func<T, long?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double> AverageAsync(Expression<Func<T, double>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double?> AverageAsync(Expression<Func<T, double?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<decimal?> AverageAsync(Expression<Func<T, decimal?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<decimal> SumAsync(Expression<Func<T, decimal>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<decimal?> SumAsync(Expression<Func<T, decimal?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double> SumAsync(Expression<Func<T, double>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<double?> SumAsync(Expression<Func<T, double?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<float> SumAsync(Expression<Func<T, float>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<float?> SumAsync(Expression<Func<T, float?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<int> SumAsync(Expression<Func<T, int>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<int?> SumAsync(Expression<Func<T, int?>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<long> SumAsync(Expression<Func<T, long>> selector)
        {
            throw new NotImplementedException();
        }

        public async Task<long?> SumAsync(Expression<Func<T, long?>> selector)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
