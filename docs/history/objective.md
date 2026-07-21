# Objective

To create an easy to configure, worktree and infrastructure isolation tool that can be retrofitted to any git managed repository.

# Basic Tenants

## Define a Repo

### Environment Variables

Through an intuitive UI, users must be able to point to a local repo and then define _which_ environment variables need to be overritten/injected to allow a worktree to run in full isolation. This is done by defining a set of named variables that are then transformed to override the required env vars in each .env.\* file in the repo.

**Example**
If I have a vue front end application, I may have a .env.local file like

```
DEV_PORT=3000
API_URL=http://localhost:4000
SOMETHING_ELSE=xxxxxxx
```

so I would define the variables

- DEV_PORT
- API_URL
  and define overrides for the relevant environment variables
- DEV_PORT=${sprig.DEV_PORT}
- API_URL=${sprig.API_URL}

NB: here the names happen to the same because they are simple, but you could easily do something like

- API_URL=http://localhost:${sprig.API_PORT} instead if you just want to vary the port but ensure everything else is the correct format.

### Docker compose overrides

Much like .env files, we want the ability to provide completely isolated infrastructure (like databases or message busses), but unlike .env files there is not a good way to provide non-modified versions into the worktree. Instead we will point to the docker compose file in the repo and then define overrides for each section.

**Example**
If I have a dotnet application with its own database defined as

```
services:
  postgres:
    image: postgres:17
    container_name: librarydb_postgres
    environment:
      POSTGRES_USER: library
      POSTGRES_PASSWORD: library_pass
      POSTGRES_DB: librarydb
    ports:
      - "6050:5432"
    restart: unless-stopped
```

I would want to make sure that the container_name gets a suffix to prevent collision and that I get a new port number, so I would define something akin to

```
"compose": {
    "file": "docker-compose.yml",
    "values": [
      {
        "path": [
          "services",
          "postgres",
          "container_name"
        ],
        "template": "librarydb_postgres--${sprig.workspace}"
      },
      {
        "path": [
          "services",
          "postgres",
          "ports",
          "0"
        ],
        "template": "${sprig.postgres_port}:5432"
      }
    ]
  },
```

That can be used to generate an instance specific docker compose file that can be stood up by sprig when creating a workspace.

## Defining Stacks

A stack is a collection of 1 or more repos, each seeded with deterministic, non-colliding port numbers that can be used to privide the information each repo needs to run.

When you add multiple repos it is important that each of the fields defined by the repo are populated. For this reason, stacks also define their own variables that check out non-colliding port values by doing something like

- POSTGRES_PORT=${sprig.ports[0]}
- API_PORT=${sprig.ports[1]}
- API_URL=http://${sprig.ports[1]} 
    OR API_URL=https://${sprig.API_PORTS} (using a previous variable)
  where each instance is given 2

## An instance of a stack is a workspace

When you want to create a new isolated worktree you create a workspace (which is just an instance of a stack). This will assign the needed ports to not collide with anything else via a central store.

# Technical Details

At the core of the app are these pieces

- clobbering untracked git .env files (both top and bottom to prevent framework loading differences)
- defining overrides for docker compose
- automatic creation of worktrees as sibling to the repo being targetted (to make it deterministic and findable)
- ability to safety teardown workspaces on demand, including strong tolerance to state shifting (like a worktree being deleted without the folder being deleted)

Look and feel

- avalonia application (like ../perch) using darkmode

# Questions to answer/explore

- creating a .sprig.json file in each repo makes it so tooling information is sharable between team members, but definition about stacks is less portable
  - potentially you can define a stack definition in _a_ repo that can be loaded from, but it creates a weird coupling that is hard to reason about
