#include <dlfcn.h>
#include <stdio.h>

int main(int argc, char **argv) {
    if (argc != 2) return 2;
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    const char *(*version)(void) = dlsym(library, "sqlite3_libversion");
    int (*open_db)(const char *, void **) = dlsym(library, "sqlite3_open");
    int (*exec_sql)(void *, const char *, void *, void *, char **) = dlsym(library, "sqlite3_exec");
    int (*prepare)(void *, const char *, int, void **, const char **) = dlsym(library, "sqlite3_prepare_v2");
    int (*step)(void *) = dlsym(library, "sqlite3_step");
    int (*column_int)(void *, int) = dlsym(library, "sqlite3_column_int");
    int (*finalize)(void *) = dlsym(library, "sqlite3_finalize");
    int (*close_db)(void *) = dlsym(library, "sqlite3_close");
    if (!version || !open_db || !exec_sql || !prepare || !step || !column_int || !finalize || !close_db) return 3;
    void *db = NULL;
    void *statement = NULL;
    if (open_db(":memory:", &db) != 0) return 4;
    if (exec_sql(db, "CREATE TABLE settings(value INTEGER); INSERT INTO settings VALUES(42);", NULL, NULL, NULL) != 0) return 5;
    if (prepare(db, "SELECT value FROM settings", -1, &statement, NULL) != 0) return 6;
    if (step(statement) != 100 || column_int(statement, 0) != 42) return 7;
    finalize(statement);
    close_db(db);
    printf("PASS: SQLite %s loads and persists/reads settings.\n", version());
    dlclose(library);
    return 0;
}
