#!/bin/bash

# Exit immediately if a command exits with a non-zero status.
set -e

# Define the full path to sqlcmd. The mssql-tools are usually in /opt/mssql-tools/bin/
SQLCMD="/opt/mssql-tools/bin/sqlcmd"
# The SQL script to run, expected to be in the same directory within the container
SQL_SCRIPT="/docker-entrypoint-initdb.d/create_database.sql"

# Wait for SQL Server to be ready
# We'll use a loop to poll the server status by trying to execute a simple query.
# The SA_PASSWORD environment variable is set by the main Docker entrypoint for MSSQL.
echo "$(date +%F_%T) [INIT.SH]: Waiting for SQL Server to start..."
MAX_TRIES=60
CURRENT_TRY=0

until ${SQLCMD} -S localhost -U sa -P "${SA_PASSWORD}" -Q "SELECT 1" > /dev/null 2>&1; do
    CURRENT_TRY=$((CURRENT_TRY + 1))
    if [ ${CURRENT_TRY} -ge ${MAX_TRIES} ]; then
        echo "$(date +%F_%T) [INIT.SH]: ERROR - SQL Server did not become ready after ${MAX_TRIES} attempts. Exiting."
        exit 1
    fi
    echo "$(date +%F_%T) [INIT.SH]: SQL Server is unavailable - sleeping for 5 seconds (Attempt ${CURRENT_TRY}/${MAX_TRIES})"
    sleep 5
done

echo "$(date +%F_%T) [INIT.SH]: SQL Server is up and running!"

# Run the main SQL script
if [ -f "${SQL_SCRIPT}" ]; then
    echo "$(date +%F_%T) [INIT.SH]: Executing SQL script: ${SQL_SCRIPT}"
    ${SQLCMD} -S localhost -U sa -P "${SA_PASSWORD}" -C -i "${SQL_SCRIPT}"
    echo "$(date +%F_%T) [INIT.SH]: SQL script execution finished."
else
    echo "$(date +%F_%T) [INIT.SH]: ERROR - SQL script ${SQL_SCRIPT} not found."
    exit 1
fi

echo "$(date +%F_%T) [INIT.SH]: Initialization complete."
exit 0
