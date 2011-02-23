﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MongoDB.Driver;
using System.Linq.Expressions;
using MongoDB.Bson;
using System.Collections;

namespace EasyMongo
{
    public class EntityCollection<TEntity> : ICountableQueryableCollection<TEntity> where TEntity : class, new()
    {
        private static EntityMapperCache<TEntity> s_mapperCache = new EntityMapperCache<TEntity>();

        public EntityCollection(MongoDatabase database, IEntityDescriptor<TEntity> descriptor, bool entityTrackingEnabled)
        {
            this.Descriptor = descriptor;
            this.Database = database;
            this.EntityTrackingEnabled = entityTrackingEnabled;

            this.m_mapper = s_mapperCache.Get(descriptor);
        }

        public bool EntityTrackingEnabled { get; private set; }

        public MongoDatabase Database { get; private set; }

        public IEntityDescriptor<TEntity> Descriptor { get; private set; }

        private EntityMapper<TEntity> m_mapper;

        public TEntity Get(Expression<Func<TEntity, bool>> predicate)
        {
            var mapper = this.m_mapper;

            var predicateDoc = mapper.GetPredicate(predicate.Body);
            var fieldsDoc = mapper.GetFields(null);
            var collection = mapper.GetCollection(this.Database);

            var doc = collection.Find(predicateDoc).SetFields(fieldsDoc).SetLimit(1).FirstOrDefault();
            if (doc == null) return default(TEntity);

            var entity = mapper.GetEntity(doc);
            this.TrackEntityState(entity);
            return (TEntity)entity;
        }

        public void InsertOnSubmit(TEntity entity)
        {
            if (entity == null) throw new ArgumentNullException();

            this.EnsureItemsToInsertCreated();
            this.m_itemsToInsert.Add(entity);
        }

        public void Attach(TEntity entity)
        {
            this.TrackEntityState(entity);
        }

        public void DeleteOnSubmit(TEntity entity)
        {
            if (entity == null) throw new ArgumentNullException();

            if (this.m_itemsToInsert != null && this.m_itemsToInsert.Remove(entity))
            {
                return;
            }

            if (this.m_stateLoaded != null)
            {
                this.m_stateLoaded.Remove(entity);
            }

            this.EnsureItemsToDeleteCreated();
            this.m_itemsToDelete.Add(entity);
        }

        public void SubmitChanges()
        {
            using (this.Database.Server.RequestStart(this.Database))
            {
                this.DeleteEntities();
                this.UpdateEntites();
                this.InsertEntities();
            }
        }

        public void Delete(Expression<Func<TEntity, bool>> predicate)
        {
            var predicateDoc = this.m_mapper.GetPredicate(predicate.Body);
            var collection = this.m_mapper.GetCollection(this.Database);
            collection.Remove(predicateDoc, RemoveFlags.None);
        }

        public void Update(
            Expression<Func<TEntity, TEntity>> updateSpec,
            Expression<Func<TEntity, bool>> predicate)
        {
            var mapper = this.m_mapper;

            var updateDoc = mapper.GetUpdates(updateSpec.Body);
            var predicateDoc = mapper.GetPredicate(predicate.Body);
            var collection = mapper.GetCollection(this.Database);

            collection.Update(predicateDoc, updateDoc);
        }

        private void InsertEntities()
        {
            if (this.m_itemsToInsert == null) return;

            var documents = this.m_itemsToInsert.Select(this.m_mapper.GetDocument).ToList();
            var collection = this.m_mapper.GetCollection(this.Database);
            collection.InsertBatch(documents);

            foreach (var entity in this.m_itemsToInsert)
            {
                this.TrackEntityState(entity);
            }

            this.m_itemsToInsert = null;
        }

        private void UpdateEntites()
        {
            if (this.m_stateLoaded == null) return;

            var updateResults = new Dictionary<TEntity, SafeModeResult>(this.m_stateLoaded.Count);

            foreach (var pair in this.m_stateLoaded.ToList())
            {
                var entity = pair.Key;
                var originalState = pair.Value;
                var mapper = this.m_mapper;

                var currentState = mapper.GetEntityState(entity);
                var updateDoc = mapper.GetStateChanged(entity, originalState, currentState);
                if (updateDoc.ElementCount == 0) continue;

                var identityDoc = mapper.GetIdentity(entity);
                if (mapper.Versioning)
                {
                    mapper.UpdateVersion(entity);
                    mapper.SetVersionCondition(updateDoc, entity);
                }
                
                var collection = mapper.GetCollection(this.Database);

                if (mapper.Versioning)
                {
                    updateResults.Add(entity, collection.Update(identityDoc, updateDoc, SafeMode.True));
                }
                else
                {
                    collection.Update(identityDoc, updateDoc);
                }

                this.m_stateLoaded[entity] = currentState;
            }

            foreach (var pair in updateResults)
            {
                if (!pair.Value.UpdatedExisting)
                { 

                }
            }
        }

        private void DeleteEntities()
        {
            if (this.m_itemsToDelete == null) return;

            foreach (var entity in this.m_itemsToDelete)
            {
                var identityDoc = this.m_mapper.GetIdentity(entity);
                var collection = this.m_mapper.GetCollection(this.Database);
                collection.Remove(identityDoc, RemoveFlags.Single);
            }

            this.m_itemsToDelete = null;
        }

        private void TrackEntityState(TEntity entity)
        {
            if (!this.EntityTrackingEnabled) return;

            this.EnsureStateLoadedCreated();

            var state = this.m_mapper.GetEntityState(entity);
            this.m_stateLoaded.Add(entity, state);
        }

        private List<TEntity> m_itemsToDelete;
        private void EnsureItemsToDeleteCreated()
        {
            if (this.m_itemsToDelete == null)
            {
                this.m_itemsToDelete = new List<TEntity>();
            }
        }

        private List<TEntity> m_itemsToInsert;
        private void EnsureItemsToInsertCreated()
        {
            if (this.m_itemsToInsert == null)
            {
                this.m_itemsToInsert = new List<TEntity>();
            }
        }

        private Dictionary<TEntity, EntityState> m_stateLoaded;
        private void EnsureStateLoadedCreated()
        {
            if (this.m_stateLoaded == null)
            {
                this.m_stateLoaded = new Dictionary<TEntity, EntityState>();
            }
        }

        public IEnumerator<TEntity> GetEnumerator()
        {
            return Query<TEntity>.GetQuery(this).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        internal int Count(Expression predicate)
        {
            var predicateDoc = this.m_mapper.GetPredicate(predicate);
            var collection = this.m_mapper.GetCollection(this.Database);
            return collection.Count(predicateDoc);
        }

        internal List<TEntity> Load(Expression predicate, int skip, int? limit, List<SortOrder> sortOrders, List<QueryHint> hints, Expression selector)
        {
            var mapper = this.m_mapper;
            var predicateDoc = mapper.GetPredicate(predicate);
            var fieldsDoc = mapper.GetFields(selector);
            var sortDoc = mapper.GetSortOrders(sortOrders);

            var collection = mapper.GetCollection(this.Database);

            var mongoCursor = collection.Find(predicateDoc).SetFields(fieldsDoc).SetSortOrder(sortDoc).SetSkip(skip);

            if (limit.HasValue)
            {
                mongoCursor = mongoCursor.SetLimit(limit.Value);
            }

            if (hints != null && hints.Count > 0)
            {
                var hintsDoc = mapper.GetHints(hints);
                mongoCursor = mongoCursor.SetHint(hintsDoc);
            }

            var docList = mongoCursor.ToList();

            var result = new List<TEntity>(docList.Count);
            foreach (var doc in docList)
            {
                var entity = mapper.GetEntity(doc);
                this.TrackEntityState(entity);
                result.Add(entity);
            }

            return result;
        }
    }
}
